using BarkCloud.TestKit;
using BarkCloud.Torrent.Infrastructure;

using System.Collections.Concurrent;

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
    private byte[] _secondTorrentBytes = [];

    public TestEngine Engine { get; } = new();

    /// <summary>Файл данных торрента — по нему видно, удалены ли скачанные данные.</summary>
    public string DataFile => Path.Combine(_root, "dl", "a.bin");
    public byte[] TorrentBytes => _torrentBytes;
    public byte[] SecondTorrentBytes => _secondTorrentBytes;

    private string SavePath => Path.Combine(_root, "dl");

    public async Task InitializeAsync()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(SavePath);
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), new byte[64 * 1024]);
        var secondSource = Path.Combine(_root, "src-second");
        Directory.CreateDirectory(secondSource);
        await File.WriteAllBytesAsync(Path.Combine(secondSource, "b.bin"), Enumerable.Repeat((byte)1, 64 * 1024).ToArray());

        var dict = await new TorrentCreator().CreateAsync(new TorrentFileSource(source));
        _torrentBytes = dict.Encode();
        var secondDict = await new TorrentCreator().CreateAsync(new TorrentFileSource(secondSource));
        _secondTorrentBytes = secondDict.Encode();

        await Engine.InitializeAsync(Path.Combine(_root, "cache"), peerPort: 0);
    }

    /// <summary>Добавляет тот же торрент под новым id; повторное добавление при живом менеджере — <see cref="DuplicateTorrentException"/>.</summary>
    public async Task<TorrentEngineService.ManagedTorrent> AddAsync(Guid? id = null)
    {
        await File.WriteAllBytesAsync(DataFile, new byte[64 * 1024]);
        return await Engine.AddTorrentFileAsync(id ?? Guid.NewGuid(), _torrentBytes, SavePath, start: false);
    }

    public async Task<TorrentEngineService.ManagedTorrent> AddSecondAsync(Guid? id = null)
    {
        var savePath = Path.Combine(_root, "dl-second");
        Directory.CreateDirectory(savePath);
        await File.WriteAllBytesAsync(Path.Combine(savePath, "b.bin"), Enumerable.Repeat((byte)1, 64 * 1024).ToArray());
        return await Engine.AddTorrentFileAsync(id ?? Guid.NewGuid(), _secondTorrentBytes, savePath, start: false);
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
    private readonly ConcurrentDictionary<TorrentManager, TorrentState> _states = new();
    private readonly ConcurrentQueue<string> _actions = new();

    public Func<TorrentManager, Task>? StopHook { get; set; }
    public Func<TorrentManager, RemoveMode, Task>? RemoveHook { get; set; }
    public Func<TorrentManager, Task>? PauseHook { get; set; }
    public Func<TorrentManager, Task>? StartHook { get; set; }

    public int StopCalls { get; private set; }
    public int RemoveCalls { get; private set; }
    public int PauseCalls { get; private set; }
    public int StartCalls { get; private set; }
    public string? LastAction { get; private set; }
    public IReadOnlyCollection<string> Actions => _actions.ToArray();

    public void SetState(TorrentManager manager, TorrentState state) => _states[manager] = state;

    public Task<bool> ApplyPausedForTestAsync(Guid id, bool paused) =>
        RunExclusiveAsync(id, managed => base.ApplyPausedAsync(managed, paused));

    public TorrentState CurrentState(TorrentManager manager) => StateOf(manager);

    protected override TorrentState StateOf(TorrentManager manager) => _states.GetOrAdd(manager, static torrent => torrent.State);

    public Task RemoveViaEngineAsync(TorrentManager manager, RemoveMode mode) => base.RemoveManagerAsync(manager, mode);

    protected override Task StopManagerAsync(TorrentManager manager)
    {
        if (StateOf(manager) is TorrentState.Stopped or TorrentState.Error)
            return Task.CompletedTask;

        StopCalls++;
        return RunActionAsync(manager, "Stop", TorrentState.Stopped, StopHook);
    }

    protected override Task RemoveManagerAsync(TorrentManager manager, RemoveMode mode)
    {
        RemoveCalls++;
        return RemoveHook != null ? RemoveHook(manager, mode) : base.RemoveManagerAsync(manager, mode);
    }

    protected override Task PauseManagerAsync(TorrentManager manager)
    {
        PauseCalls++;
        return RunActionAsync(manager, "Pause", TorrentState.Paused, PauseHook);
    }

    protected override Task StartManagerAsync(TorrentManager manager)
    {
        StartCalls++;
        return RunActionAsync(manager, "Start", TorrentState.Downloading, StartHook);
    }

    private async Task RunActionAsync(TorrentManager manager, string action, TorrentState state, Func<TorrentManager, Task>? hook)
    {
        LastAction = action;
        _actions.Enqueue(action);
        if (hook != null)
            await hook(manager);
        SetState(manager, state);
    }
}
