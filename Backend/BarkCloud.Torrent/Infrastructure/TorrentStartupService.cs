using BarkCloud.Torrent.Persistence;

using MonoTorrent;

namespace BarkCloud.Torrent.Infrastructure;

/// <summary>
/// При старте: инициализирует движок и пере-добавляет торренты из БД (по magnet/.torrent),
/// восстанавливая приоритеты файлов и возобновляя незавершённые (кроме приостановленных).
/// Fast-resume подхватывается движком из CacheDirectory автоматически.
/// </summary>
public class TorrentStartupService : IHostedService
{
    private readonly TorrentEngineService _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TorrentStartupService> _logger;

    public TorrentStartupService(
        TorrentEngineService engine,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TorrentStartupService> logger)
    {
        _engine = engine;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var downloadPath = _configuration["Torrent:DownloadPath"] ?? "/mnt/torrents";
        var peerPort = int.TryParse(_configuration["Torrent:PeerPort"], out var p) && p > 0 ? p : 6881;

        await _engine.InitializeAsync(Path.Combine(downloadPath, ".cache"), peerPort);

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITorrentStore>();
        var all = await store.ListAll();

        foreach (var entity in all)
        {
            try
            {
                var start = !entity.Paused;
                TorrentEngineService.ManagedTorrent managed;

                if (!string.IsNullOrEmpty(entity.MagnetUri))
                    managed = await _engine.AddMagnetAsync(entity.Id, entity.MagnetUri, entity.SavePath, start);
                else if (entity.TorrentFile is { Length: > 0 })
                    managed = await _engine.AddTorrentFileAsync(entity.Id, entity.TorrentFile, entity.SavePath, start);
                else
                    continue;

                await ReapplyPrioritiesAsync(managed, entity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось восстановить торрент {Id}", entity.Id);
            }
        }

        _logger.LogInformation("Восстановлено торрентов: {Count}", all.Count);
    }

    private static async Task ReapplyPrioritiesAsync(TorrentEngineService.ManagedTorrent managed, Domain.TorrentEntity entity)
    {
        foreach (var f in entity.Files)
        {
            if (f.Index >= 0 && f.Index < managed.Manager.Files.Count)
                await managed.Manager.SetFilePriorityAsync(
                    managed.Manager.Files[f.Index],
                    TorrentMapper.ToMonoTorrentPriority((BarkCloud.Proto.Torrent.TorrentFilePriority)f.Priority));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
