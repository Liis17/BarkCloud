using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Torrent;
using BarkCloud.TestKit;
using BarkCloud.Torrent.Domain;
using BarkCloud.Torrent.Host;
using BarkCloud.Torrent.Persistence;
using BarkCloud.Torrent.Tests._Helpers;

using Microsoft.Extensions.Configuration;

namespace BarkCloud.Torrent.Tests.Host;

public class TorrentApiServiceTests : IAsyncLifetime
{
    private const long UserId = 7;

    private readonly EngineHarness _harness = new();
    private readonly Mock<ITorrentStore> _store = new();
    private readonly TorrentEntity _entity = new() { Id = Guid.NewGuid(), UserId = UserId };
    private readonly TestServerCallContext _context = new();
    private TorrentApiService _service = null!;

    private TestEngine Engine => _harness.Engine;

    private TorrentIdRequest IdRequest => new() { Id = _entity.Id.ToString() };

    public async Task InitializeAsync()
    {
        await _harness.InitializeAsync();
        await _harness.AddAsync(_entity.Id);

        _store.Setup(s => s.Get(_entity.Id, UserId)).ReturnsAsync(_entity);
        _service = new TorrentApiService(
            UserContextFactory.Create(UserId),
            new TokenRevocationCache(),
            _store.Object,
            Engine,
            import: null!,
            scopeFactory: null!,
            new ConfigurationBuilder().Build(),
            new MetricsCollector());
    }

    public Task DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task PauseTorrent_WhenEngineFails_LeavesStoredFlagUntouched()
    {
        Engine.PauseHook = _ => throw new IOException("pause failed");

        await ((Func<Task>)(() => _service.PauseTorrent(IdRequest, _context))).Should().ThrowAsync<IOException>();

        _entity.Paused.Should().BeFalse();
        _store.Verify(s => s.SaveChanges(), Times.Never);
    }

    [Fact]
    public async Task PauseTorrent_SavesFlagOnlyAfterEngineSucceeded()
    {
        var enginePaused = false;
        var pausedAtSave = false;
        Engine.PauseHook = _ => { enginePaused = true; return Task.CompletedTask; };
        _store.Setup(s => s.SaveChanges()).Callback(() => pausedAtSave = enginePaused).Returns(Task.CompletedTask);

        await _service.PauseTorrent(IdRequest, _context);

        _entity.Paused.Should().BeTrue();
        pausedAtSave.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeTorrent_WhenEngineFails_LeavesStoredFlagUntouched()
    {
        _entity.Paused = true;
        Engine.StartHook = _ => throw new IOException("start failed");

        await ((Func<Task>)(() => _service.ResumeTorrent(IdRequest, _context))).Should().ThrowAsync<IOException>();

        _entity.Paused.Should().BeTrue();
        _store.Verify(s => s.SaveChanges(), Times.Never);
    }

    [Fact]
    public async Task ResumeTorrent_SavesFlagOnlyAfterEngineSucceeded()
    {
        _entity.Paused = true;
        var engineStarted = false;
        var startedAtSave = false;
        Engine.StartHook = _ => { engineStarted = true; return Task.CompletedTask; };
        _store.Setup(s => s.SaveChanges()).Callback(() => startedAtSave = engineStarted).Returns(Task.CompletedTask);

        await _service.ResumeTorrent(IdRequest, _context);

        _entity.Paused.Should().BeFalse();
        startedAtSave.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveTorrent_WhenEngineFails_KeepsRowAndRetryRemovesItOnce()
    {
        var request = new RemoveTorrentRequest { Id = _entity.Id.ToString(), DeleteFiles = true };
        Engine.RemoveHook = (_, _) => throw new IOException("remove failed");

        await ((Func<Task>)(() => _service.RemoveTorrent(request, _context))).Should().ThrowAsync<IOException>();

        _store.Verify(s => s.Remove(It.IsAny<TorrentEntity>()), Times.Never);
        Engine.Get(_entity.Id).Should().NotBeNull();

        Engine.RemoveHook = null;
        await _service.RemoveTorrent(request, _context);

        _store.Verify(s => s.Remove(_entity), Times.Once);
        Engine.Get(_entity.Id).Should().BeNull();
    }
}
