using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Torrent;
using BarkCloud.TestKit;
using BarkCloud.Torrent.Domain;
using BarkCloud.Torrent.Host;
using BarkCloud.Torrent.Persistence;
using BarkCloud.Torrent.Tests._Helpers;

using Grpc.Core;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using MonoTorrent.Client;

namespace BarkCloud.Torrent.Tests.Host;

public class TorrentApiServiceTests : IAsyncLifetime
{
    private const long UserId = 7;

    private readonly EngineHarness _harness = new();
    private readonly Mock<ITorrentStore> _store = new();
    private readonly TorrentEntity _entity = new() { Id = Guid.NewGuid(), UserId = UserId };
    private readonly TestServerCallContext _context = new();
    private ServiceProvider _services = null!;
    private TorrentApiService _service = null!;

    private TestEngine Engine => _harness.Engine;
    private IServiceScopeFactory ScopeFactory => _services.GetRequiredService<IServiceScopeFactory>();

    private TorrentIdRequest IdRequest => new() { Id = _entity.Id.ToString() };

    public async Task InitializeAsync()
    {
        await _harness.InitializeAsync();
        var managed = await _harness.AddAsync(_entity.Id);
        Engine.SetState(managed.Manager, TorrentState.Downloading);

        _store.Setup(s => s.Get(_entity.Id, UserId)).ReturnsAsync(_entity);
        _store.Setup(s => s.GetPaused(_entity.Id, UserId, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<bool?>(_entity.Paused));
        _store.Setup(s => s.SetPaused(_entity.Id, UserId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, long, bool, CancellationToken>((_, _, paused, _) =>
            {
                _entity.Paused = paused;
                return Task.FromResult(true);
            });
        _services = new ServiceCollection()
            .AddScoped<ITorrentStore>(_ => _store.Object)
            .BuildServiceProvider();
        _service = new TorrentApiService(
            UserContextFactory.Create(UserId),
            new TokenRevocationCache(),
            _store.Object,
            Engine,
            import: null!,
            scopeFactory: ScopeFactory,
            new ConfigurationBuilder().Build(),
            new MetricsCollector());
    }

    public async Task DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task PauseTorrent_WhenEngineFails_LeavesStoredFlagUntouched()
    {
        var managed = Engine.Get(_entity.Id)!;
        Engine.PauseHook = _ => throw new IOException("pause failed");

        await ((Func<Task>)(() => _service.PauseTorrent(IdRequest, _context))).Should().ThrowAsync<IOException>();

        _entity.Paused.Should().BeFalse();
        _store.Verify(s => s.SetPaused(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        managed.PausedReconcilePending.Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Downloading);
    }

    [Fact]
    public async Task PauseTorrent_SavesFlagOnlyAfterEngineSucceeded()
    {
        var enginePaused = false;
        Engine.PauseHook = _ => { enginePaused = true; return Task.CompletedTask; };

        await _service.PauseTorrent(IdRequest, _context);

        _entity.Paused.Should().BeTrue();
        enginePaused.Should().BeTrue();
        Engine.LastAction.Should().Be("Pause");
    }

    [Fact]
    public async Task ResumeTorrent_WhenEngineFails_LeavesStoredFlagUntouched()
    {
        _entity.Paused = true;
        var managed = Engine.Get(_entity.Id)!;
        Engine.SetState(managed.Manager, TorrentState.Paused);
        Engine.StartHook = _ => throw new IOException("start failed");

        await ((Func<Task>)(() => _service.ResumeTorrent(IdRequest, _context))).Should().ThrowAsync<IOException>();

        _entity.Paused.Should().BeTrue();
        _store.Verify(s => s.SetPaused(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        managed.PausedReconcilePending.Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Paused);
    }

    [Fact]
    public async Task ResumeTorrent_SavesFlagOnlyAfterEngineSucceeded()
    {
        _entity.Paused = true;
        var engineStarted = false;
        Engine.StartHook = _ => { engineStarted = true; return Task.CompletedTask; };
        Engine.SetState(Engine.Get(_entity.Id)!.Manager, TorrentState.Paused);

        await _service.ResumeTorrent(IdRequest, _context);

        _entity.Paused.Should().BeFalse();
        engineStarted.Should().BeTrue();
        Engine.LastAction.Should().Be("Start");
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

    [Fact]
    public async Task StreamProgress_SessionRevokedBySessionThreshold_ThrowsUnauthenticated()
    {
        var revocations = new TokenRevocationCache();
        revocations.Revoke(UserId, "device-1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), maxSessionId: 5);
        var service = new TorrentApiService(
            UserContextFactory.Create(UserId, sessionId: 5),
            revocations,
            _store.Object,
            Engine,
            import: null!,
            scopeFactory: ScopeFactory,
            new ConfigurationBuilder().Build(),
            new MetricsCollector());

        var act = () => service.StreamProgress(
            new StreamProgressRequest(), Mock.Of<IServerStreamWriter<TorrentProgressSnapshot>>(), _context);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }
}
