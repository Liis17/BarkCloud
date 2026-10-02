using BarkCloud.TestKit;
using BarkCloud.Torrent.Infrastructure;

using MonoTorrent;
using MonoTorrent.Client;

namespace BarkCloud.Torrent.Tests._Helpers;

/// <summary>
/// Настоящий <see cref="ClientEngine"/> без сети (торренты добавляются с <c>start: false</c>) и временный
/// каталог с данными. Отказы вставляются через швы <see cref="TestEngine"/>.
/// </summary>
internal sealed class EngineHarness : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bark-torrent-" + Guid.NewGuid().ToString("N"));
    private byte[] _torrentBytes = [];

    public TestEngine Engine { get; } = new();

    /// <summary>Файл данных торрента — по нему видно, удалены ли скачанные данные.</summary>
    public string DataFile => Path.Combine(_root, "dl", "a.bin");

    private string SavePath => Path.Combine(_root, "dl");

    public async Task InitializeAsync()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(SavePath);
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), new byte[64 * 1024]);

        var dict = await new TorrentCreator().CreateAsync(new TorrentFileSource(source));
        _torrentBytes = dict.Encode();

        await Engine.InitializeAsync(Path.Combine(_root, "cache"), peerPort: 0);
    }

    /// <summary>Добавляет тот же торрент под новым id; повторное добавление при живом менеджере — <see cref="DuplicateTorrentException"/>.</summary>
    public async Task<TorrentEngineService.ManagedTorrent> AddAsync(Guid? id = null)
    {
        await File.WriteAllBytesAsync(DataFile, new byte[64 * 1024]);
        return await Engine.AddTorrentFileAsync(id ?? Guid.NewGuid(), _torrentBytes, SavePath, start: false);
    }

    public async Task DisposeAsync()
    {
        await Engine.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Швы <see cref="TorrentEngineService"/>: хук заменяет вызов MonoTorrent, без хука работает настоящий движок.</summary>
internal sealed class TestEngine() : TorrentEngineService(TestLoggers.Null<TorrentEngineService>())
{
    public Func<TorrentManager, Task>? StopHook { get; set; }
    public Func<TorrentManager, RemoveMode, Task>? RemoveHook { get; set; }
    public Func<TorrentManager, Task>? PauseHook { get; set; }
    public Func<TorrentManager, Task>? StartHook { get; set; }

    public int StopCalls { get; private set; }
    public int RemoveCalls { get; private set; }

    public Task RemoveViaEngineAsync(TorrentManager manager, RemoveMode mode) => base.RemoveManagerAsync(manager, mode);

    protected override Task StopManagerAsync(TorrentManager manager)
    {
        StopCalls++;
        return StopHook != null ? StopHook(manager) : base.StopManagerAsync(manager);
    }

    protected override Task RemoveManagerAsync(TorrentManager manager, RemoveMode mode)
    {
        RemoveCalls++;
        return RemoveHook != null ? RemoveHook(manager, mode) : base.RemoveManagerAsync(manager, mode);
    }

    protected override Task PauseManagerAsync(TorrentManager manager)
        => PauseHook != null ? PauseHook(manager) : base.PauseManagerAsync(manager);

    protected override Task StartManagerAsync(TorrentManager manager)
        => StartHook != null ? StartHook(manager) : base.StartManagerAsync(manager);
}
