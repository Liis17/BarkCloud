using BarkCloud.Proto.Torrent;
using BarkCloud.Torrent.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Torrent.Infrastructure;

/// <summary>
/// Раз в 5 с переносит живую статистику движка в БД (трафик — накопительно поверх сессионных счётчиков,
/// чтобы «скачано/отдано» и ratio переживали рестарт), а также обновляет счётчики пиров.
/// Baseline сессионных счётчиков сдвигается до снятого снимка только после успешного SaveChanges.
/// </summary>
public class TorrentPersistenceService : BackgroundService
{
    private readonly TorrentEngineService _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TorrentPersistenceService> _logger;

    public TorrentPersistenceService(
        TorrentEngineService engine,
        IServiceScopeFactory scopeFactory,
        ILogger<TorrentPersistenceService> logger)
    {
        _engine = engine;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при сохранении статистики торрентов");
            }

            try
            {
                await _engine.ReconcilePausedAsync(ReadPausedAsync, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при согласовании состояния Paused");
            }

            // Диагностический лог раз в 30 с (каждые 6 тиков по 5 с).
            if (++tick % 6 == 0)
                LogEngineState();

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void LogEngineState()
    {
        var all = _engine.All.ToList();
        if (all.Count == 0)
            return;

        foreach (var managed in all)
        {
            var m = managed.Manager;
            _logger.LogInformation(
                "[Torrent] {Name} | State={State} Progress={Progress:P0} " +
                "Down={DownKB:F0}KB/s Up={UpKB:F0}KB/s | " +
                "Seeds={Seeds} Leeches={Leeches} Available={Available}",
                string.IsNullOrEmpty(m.Torrent?.Name) ? managed.Id.ToString()[..8] : m.Torrent!.Name,
                m.State,
                m.Progress / 100.0,
                m.Monitor.DownloadRate / 1024.0,
                m.Monitor.UploadRate / 1024.0,
                m.Peers.Seeds,
                m.Peers.Leechs,
                m.Peers.Available);
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var managedList = _engine.All.ToList();
        if (managedList.Count == 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TorrentContext>();
        var checkpoints = new List<(TorrentEngineService.ManagedTorrent Managed,
            TorrentEngineService.ManagedTorrent.TrafficCheckpoint Checkpoint,
            long BaseDownloaded, long BaseUploaded)>();

        foreach (var managed in managedList)
        {
            var entity = await context.Torrents.FindAsync(new object?[] { managed.Id }, ct);
            if (entity == null)
                continue;

            var m = managed.Manager;

            // Живые счётчики пиров.
            managed.Seeds = m.Peers.Seeds;
            managed.Leechers = m.Peers.Leechs;

            // Накопительный трафик: приращение сессионного счётчика движка.
            var (baseDown, baseUp) = managed.BaselineFor(entity.Downloaded, entity.Uploaded);
            var (sessionDown, sessionUp) = ReadTraffic(managed);
            entity.Downloaded += Math.Max(0, sessionDown - baseDown);
            entity.Uploaded += Math.Max(0, sessionUp - baseUp);

            entity.Progress = m.Progress / 100.0;
            entity.Status = (int)TorrentMapper.MapStatus(m.State, m.Complete, entity.Paused);
            if (m.HasMetadata && m.Torrent != null)
            {
                entity.TotalSize = m.Torrent.Size;
                if (string.IsNullOrEmpty(entity.Name))
                    entity.Name = m.Torrent.Name;
            }

            if (m.Complete && entity.CompletedAt == null)
                entity.CompletedAt = DateTime.UtcNow;

            checkpoints.Add((managed,
                new TorrentEngineService.ManagedTorrent.TrafficCheckpoint(
                    entity.Downloaded, entity.Uploaded, sessionDown, sessionUp),
                baseDown, baseUp));
        }

        // Не меняем состояние движка, пока все счётчики batch не прочитаны: поздняя ошибка чтения
        // не должна затереть checkpoint предыдущего неподтверждённого flush.
        foreach (var (managed, checkpoint, baseDown, baseUp) in checkpoints)
        {
            managed.LastSessionDownloaded = baseDown;
            managed.LastSessionUploaded = baseUp;
            managed.PendingFlush = checkpoint.SessionDownloaded == baseDown && checkpoint.SessionUploaded == baseUp
                ? null
                : checkpoint;
        }

        await context.SaveChangesAsync(ct);

        // Снимок фиксируется как baseline только после подтверждённого сохранения.
        foreach (var (managed, checkpoint, _, _) in checkpoints)
        {
            managed.LastSessionDownloaded = checkpoint.SessionDownloaded;
            managed.LastSessionUploaded = checkpoint.SessionUploaded;
            managed.PendingFlush = null;
        }
    }

    private async Task<bool?> ReadPausedAsync(Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TorrentContext>();
        return await context.Torrents.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => (bool?)t.Paused)
            .FirstOrDefaultAsync(ct);
    }

    protected virtual (long Received, long Sent) ReadTraffic(TorrentEngineService.ManagedTorrent managed)
        => (managed.Manager.Monitor.DataBytesReceived, managed.Manager.Monitor.DataBytesSent);
}
