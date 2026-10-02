using System.Collections.Concurrent;
using System.Net;

using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace BarkCloud.Torrent.Infrastructure;

/// <summary>
/// Singleton-обёртка над MonoTorrent <see cref="ClientEngine"/>: один движок на процесс,
/// торренты сопоставлены нашему Guid-идентификатору. Хранит живую статистику пиров
/// (сиды/личи из последнего ответа трекера). Пер-пользовательская изоляция — на уровне
/// SavePath ({DownloadPath}/{userId}) и фильтрации в БД, здесь движок глобальный.
/// </summary>
public class TorrentEngineService : IAsyncDisposable
{
    private readonly ILogger<TorrentEngineService> _logger;
    private ClientEngine? _engine;
    private readonly ConcurrentDictionary<Guid, ManagedTorrent> _managed = new();

    public TorrentEngineService(ILogger<TorrentEngineService> logger) => _logger = logger;

    public sealed class ManagedTorrent
    {
        public required Guid Id { get; init; }
        public required TorrentManager Manager { get; init; }
        public int Seeds;
        public int Leechers;
        // Для накопления суммарного трафика поверх сессионных счётчиков движка (переживают рестарт в БД).
        public long LastSessionDownloaded;
        public long LastSessionUploaded;
        // Сериализует Pause/Resume/Remove одного торрента.
        internal readonly SemaphoreSlim Gate = new(1, 1);
    }

    public Task InitializeAsync(string cacheDirectory, int peerPort)
    {
        Directory.CreateDirectory(cacheDirectory);

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AutoSaveLoadDhtCache = true,

            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Any, peerPort) },
                { "ipv6", new IPEndPoint(IPAddress.IPv6Any, peerPort) },
            },

            // DHT на том же UDP-порту — больше пиров без трекера.
            DhtEndPoint = new IPEndPoint(IPAddress.Any, peerPort),

            // Выше лимиты соединений — больше пиров одновременно.
            MaximumConnections = 500,
            MaximumHalfOpenConnections = 50,

            // RC4-шифрование первым — обход DPI-throttling у некоторых провайдеров.
            AllowedEncryption = [EncryptionType.RC4Full, EncryptionType.RC4Header, EncryptionType.PlainText],

            // 32 МБ дискового кеша снижает I/O задержки при записи частей.
            DiskCacheBytes = 32 * 1024 * 1024,
        }.ToSettings();

        _engine = new ClientEngine(settings);
        _logger.LogInformation("Торрент-движок запущен, peer-порт {Port}, кэш {Cache}", peerPort, cacheDirectory);
        return Task.CompletedTask;
    }

    private ClientEngine Engine => _engine ?? throw new InvalidOperationException("Движок не инициализирован");

    public IEnumerable<ManagedTorrent> All => _managed.Values;

    public ManagedTorrent? Get(Guid id) => _managed.TryGetValue(id, out var m) ? m : null;

    public async Task<ManagedTorrent> AddMagnetAsync(Guid id, string magnetUri, string savePath, bool start)
    {
        Directory.CreateDirectory(savePath);
        var link = MagnetLink.Parse(magnetUri);
        TorrentManager manager;
        try
        {
            manager = await Engine.AddAsync(link, savePath);
        }
        catch (TorrentException ex) when (ex.Message.Contains("already been registered"))
        {
            throw new DuplicateTorrentException(ex);
        }
        var managed = Track(id, manager);
        if (start)
            await manager.StartAsync();
        return managed;
    }

    public async Task<ManagedTorrent> AddTorrentFileAsync(Guid id, byte[] torrentBytes, string savePath, bool start)
    {
        Directory.CreateDirectory(savePath);
        var torrent = await MonoTorrent.Torrent.LoadAsync(torrentBytes);
        TorrentManager manager;
        try
        {
            manager = await Engine.AddAsync(torrent, savePath);
        }
        catch (TorrentException ex) when (ex.Message.Contains("already been registered"))
        {
            throw new DuplicateTorrentException(ex);
        }
        var managed = Track(id, manager);
        if (start)
            await manager.StartAsync();
        return managed;
    }

    private ManagedTorrent Track(Guid id, TorrentManager manager)
    {
        var managed = new ManagedTorrent { Id = id, Manager = manager };
        _managed[id] = managed;
        return managed;
    }

    public Task PauseAsync(Guid id) => RunExclusiveAsync(id, m => PauseManagerAsync(m.Manager));

    public Task ResumeAsync(Guid id) => RunExclusiveAsync(id, m => StartManagerAsync(m.Manager));

    /// <summary>
    /// Запись в реестре снимается только после успешного удаления из движка: при сбое торрент
    /// остаётся управляемым и вызов можно повторить. Торрента нет в реестре — no-op.
    /// </summary>
    public Task RemoveAsync(Guid id, bool deleteData) => RunExclusiveAsync(id, async m =>
    {
        await StopManagerAsync(m.Manager);

        // Сбой удаления файлов случается уже после снятия менеджера с движка. Повторный вызов
        // для снятого менеджера не бросает и доудаляет данные, поэтому повтор безопасен.
        var mode = deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly;
        await RemoveManagerAsync(m.Manager, mode);

        _managed.TryRemove(new KeyValuePair<Guid, ManagedTorrent>(id, m));
    });

    protected virtual Task PauseManagerAsync(TorrentManager manager) => manager.PauseAsync();

    protected virtual Task StartManagerAsync(TorrentManager manager) => manager.StartAsync();

    protected virtual Task StopManagerAsync(TorrentManager manager) =>
        manager.State is TorrentState.Stopped or TorrentState.Error ? Task.CompletedTask : manager.StopAsync();

    protected virtual Task RemoveManagerAsync(TorrentManager manager, RemoveMode mode) => Engine.RemoveAsync(manager, mode);

    private async Task RunExclusiveAsync(Guid id, Func<ManagedTorrent, Task> operation)
    {
        if (!_managed.TryGetValue(id, out var m))
            return;

        await m.Gate.WaitAsync();
        try
        {
            // Параллельное удаление могло завершиться, пока ждали замок.
            if (!_managed.TryGetValue(id, out var current) || !ReferenceEquals(current, m))
                return;

            await operation(m);
        }
        finally
        {
            m.Gate.Release();
        }
    }

    public async Task SetFilePriorityAsync(Guid id, int fileIndex, Priority priority)
    {
        if (!_managed.TryGetValue(id, out var m))
            return;

        if (fileIndex < 0 || fileIndex >= m.Manager.Files.Count)
            return;

        await m.Manager.SetFilePriorityAsync(m.Manager.Files[fileIndex], priority);
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine != null)
        {
            await _engine.StopAllAsync();
            _engine.Dispose();
        }
    }
}
