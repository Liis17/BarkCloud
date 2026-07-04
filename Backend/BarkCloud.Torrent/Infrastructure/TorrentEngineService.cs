using System.Collections.Concurrent;
using System.Net;

using MonoTorrent;
using MonoTorrent.Client;

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
    }

    public Task InitializeAsync(string cacheDirectory, int peerPort)
    {
        Directory.CreateDirectory(cacheDirectory);

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Any, peerPort) },
                { "ipv6", new IPEndPoint(IPAddress.IPv6Any, peerPort) },
            },
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
        var manager = await Engine.AddAsync(link, savePath);
        var managed = Track(id, manager);
        if (start)
            await manager.StartAsync();
        return managed;
    }

    public async Task<ManagedTorrent> AddTorrentFileAsync(Guid id, byte[] torrentBytes, string savePath, bool start)
    {
        Directory.CreateDirectory(savePath);
        var torrent = await MonoTorrent.Torrent.LoadAsync(torrentBytes);
        var manager = await Engine.AddAsync(torrent, savePath);
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

    public async Task PauseAsync(Guid id)
    {
        if (_managed.TryGetValue(id, out var m))
            await m.Manager.PauseAsync();
    }

    public async Task ResumeAsync(Guid id)
    {
        if (_managed.TryGetValue(id, out var m))
            await m.Manager.StartAsync();
    }

    public async Task RemoveAsync(Guid id, bool deleteData)
    {
        if (!_managed.TryRemove(id, out var m))
            return;

        var mode = deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly;
        await Engine.RemoveAsync(m.Manager, mode);
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
