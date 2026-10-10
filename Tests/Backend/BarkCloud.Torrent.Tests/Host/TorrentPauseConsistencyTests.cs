using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Torrent;
using BarkCloud.Shared.Queue.Users;
using BarkCloud.TestKit;
using BarkCloud.Torrent.Consumers;
using BarkCloud.Torrent.Domain;
using BarkCloud.Torrent.Host;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;
using BarkCloud.Torrent.Tests._Helpers;

using Grpc.Core;

using MassTransit;

using System.Collections.Concurrent;
using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using MonoTorrent.Client;

namespace BarkCloud.Torrent.Tests.Host;

public sealed class TorrentPauseConsistencyTests : IAsyncLifetime
{
    private const long UserId = 7;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(8);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bark-pause-" + Guid.NewGuid().ToString("N"));
    private readonly EngineHarness _harness = new();
    private readonly PauseCommandInterceptor _interceptor = new();
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    private TestEngine Engine => _harness.Engine;
    private IServiceScopeFactory ScopeFactory => _services.GetRequiredService<IServiceScopeFactory>();

    public async Task InitializeAsync()
    {
        await _harness.InitializeAsync();
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "torrent.sqlite")};Pooling=False;Default Timeout=8";
        _services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<TorrentContext>(options => options.UseSqlite(_connectionString).AddInterceptors(_interceptor))
            .AddScoped<ITorrentStore, TorrentStore>()
            .BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TorrentContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _services.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            File.Delete(Path.Combine(_root, "torrent.sqlite") + suffix);
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentPauseAndResume_PersistsTheLastCommand(bool firstCommandPauses)
    {
        var id = Guid.NewGuid();
        await SeedAndAddAsync(id, paused: !firstCommandPauses);
        _interceptor.HoldNextPausedUpdate(id);

        var first = SetPausedAsync(id, firstCommandPauses);
        try
        {
            await _interceptor.WaitForHeldUpdateAsync();
            var second = SetPausedAsync(id, !firstCommandPauses);
            await _interceptor.WaitForPausedReadCountAsync(3);

            await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(100)));
            second.IsCompleted.Should().BeFalse();
            Engine.Actions.Should().Equal(firstCommandPauses ? "Pause" : "Start");

            _interceptor.ReleaseHeldUpdate();
            await Task.WhenAll(first, second).WaitAsync(WaitTimeout);

            (await ReadPausedAsync(id)).Should().Be(!firstCommandPauses);
            Engine.LastAction.Should().Be(firstCommandPauses ? "Start" : "Pause");
        }
        finally
        {
            _interceptor.ReleaseHeldUpdate();
        }
    }

    [Fact]
    public async Task ResumeTorrent_UsesCurrentValueFromDatabaseWithStaleTrackedSnapshot()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        await using var context = CreateContext();
        var tracked = await context.Torrents.SingleAsync(t => t.Id == id);

        await SetPausedAsync(id, paused: true);
        tracked.Paused.Should().BeFalse();

        await CreateService(context).ResumeTorrent(IdRequest(id), new TestServerCallContext());

        tracked.Paused.Should().BeFalse();
        (await ReadPausedAsync(id)).Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Downloading);
        Engine.Actions.Should().Equal("Pause", "Start");
    }

    [Fact]
    public async Task PauseTorrent_WhenUpdateFindsDeletedRow_ReturnsNotFoundWithoutMarkingReconcile()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        _interceptor.HoldNextPausedUpdate(id);
        var pause = SetPausedAsync(id, paused: true);

        try
        {
            await _interceptor.WaitForHeldUpdateAsync();
            await DeleteTorrentAsync(id);

            _interceptor.ReleaseHeldUpdate();
            var error = await ((Func<Task>)(async () => await pause)).Should().ThrowAsync<RpcException>();
            error.Which.StatusCode.Should().Be(StatusCode.NotFound);
        }
        finally
        {
            _interceptor.ReleaseHeldUpdate();
        }

        Engine.Get(id).Should().BeSameAs(managed);
        managed.PausedReconcilePending.Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Paused);
        (await ReadPausedAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task PauseTorrent_WhenVerificationReadFindsDeletedRow_ReturnsNotFoundWithoutMarkingReconcile()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        _interceptor.FailNextUpdateBefore(new IOException("update rejected"), () => DeleteTorrentAsync(id));

        var act = () => SetPausedAsync(id, paused: true);
        var error = await act.Should().ThrowAsync<RpcException>();

        error.Which.StatusCode.Should().Be(StatusCode.NotFound);
        (await ReadPausedAsync(id)).Should().BeNull();
        Engine.Get(id).Should().BeSameAs(managed);
        managed.PausedReconcilePending.Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Paused);
    }

    [Fact]
    public async Task ReconcileMissingRow_ClearsPendingWithoutRepeating()
    {
        var (id, managed) = await CreatePendingReconcileAsync();
        await DeleteTorrentAsync(id);
        var actions = Engine.Actions.ToArray();
        var readCount = 0;
        Task<bool?> ReadMissingAsync(Guid torrentId, CancellationToken ct)
        {
            Interlocked.Increment(ref readCount);
            return ReadPausedAsync(torrentId, UserId, ct);
        }

        await Engine.ReconcilePausedAsync(ReadMissingAsync, CancellationToken.None);

        managed.PausedReconcilePending.Should().BeFalse();
        Engine.Actions.Should().Equal(actions);
        Volatile.Read(ref readCount).Should().Be(1);

        await Engine.ReconcilePausedAsync(ReadMissingAsync, CancellationToken.None);

        Volatile.Read(ref readCount).Should().Be(1);
        Engine.Actions.Should().Equal(actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateFailureBeforeCommit_RestoresPersistedFlagAndRethrows(bool requestedPaused)
    {
        var id = Guid.NewGuid();
        var originalPaused = !requestedPaused;
        var managed = await SeedAndAddAsync(id, originalPaused);
        _interceptor.FailNextUpdateBefore(new IOException("update rejected before commit"));

        var act = () => SetPausedAsync(id, requestedPaused);
        (await act.Should().ThrowAsync<IOException>()).WithMessage("update rejected before commit");

        (await ReadPausedAsync(id)).Should().Be(originalPaused);
        Engine.CurrentState(managed.Manager).Should().Be(originalPaused ? TorrentState.Paused : TorrentState.Downloading);
        managed.PausedReconcilePending.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateFailureAfterCommit_IsTreatedAsSuccess(bool requestedPaused)
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: !requestedPaused);
        _interceptor.FailNextUpdateAfter(new IOException("ack lost after commit"));

        await SetPausedAsync(id, requestedPaused);

        (await ReadPausedAsync(id)).Should().Be(requestedPaused);
        Engine.CurrentState(managed.Manager).Should().Be(requestedPaused ? TorrentState.Paused : TorrentState.Downloading);
        managed.PausedReconcilePending.Should().BeFalse();
    }

    [Fact]
    public async Task FailedUpdateAndVerificationRead_MarksPendingAndReconcilesLater()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        _interceptor.FailNextUpdateBefore(new IOException("update failed"));
        _interceptor.FailPausedRead(3, new IOException("verification read failed"));

        var act = () => SetPausedAsync(id, paused: true);
        (await act.Should().ThrowAsync<IOException>()).WithMessage("update failed");
        managed.PausedReconcilePending.Should().BeTrue();

        await Engine.ReconcilePausedAsync(ReadPausedForReconcileAsync, CancellationToken.None);

        managed.PausedReconcilePending.Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Downloading);
    }

    [Fact]
    public async Task FailedCompensation_MarksPendingAndReconcilesLater()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        _interceptor.FailNextUpdateBefore(new IOException("update failed"));
        Engine.StartHook = _ => throw new IOException("compensation failed");

        var act = () => SetPausedAsync(id, paused: true);
        (await act.Should().ThrowAsync<IOException>()).WithMessage("update failed");
        managed.PausedReconcilePending.Should().BeTrue();

        Engine.StartHook = null;
        await Engine.ReconcilePausedAsync(ReadPausedForReconcileAsync, CancellationToken.None);

        managed.PausedReconcilePending.Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Downloading);
    }

    [Fact]
    public async Task EngineActionThatChangesStateThenThrows_IsRestoredToPersistedFlag()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        Engine.PauseHook = manager =>
        {
            Engine.SetState(manager, TorrentState.Paused);
            throw new IOException("pause failed after state change");
        };

        var act = () => SetPausedAsync(id, paused: true);
        (await act.Should().ThrowAsync<IOException>()).WithMessage("pause failed after state change");

        (await ReadPausedAsync(id)).Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Downloading);
        managed.PausedReconcilePending.Should().BeFalse();
    }

    [Fact]
    public async Task SuccessfulLaterCommand_ClearsOldReconcileBeforeRetry()
    {
        var (id, managed) = await CreatePendingReconcileAsync();

        await SetPausedAsync(id, paused: false);
        var actions = Engine.Actions.ToArray();
        await Engine.ReconcilePausedAsync(ReadPausedForReconcileAsync, CancellationToken.None);

        managed.PausedReconcilePending.Should().BeFalse();
        Engine.Actions.Should().Equal(actions);
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Downloading);
    }

    [Fact]
    public async Task ReconcileWaitingBehindSuccessfulCommand_DoesNotApplyOldRetry()
    {
        var (id, managed) = await CreatePendingReconcileAsync();
        var actionsBeforeCommand = Engine.Actions.ToArray();
        _interceptor.HoldNextPausedUpdate(id);
        var command = SetPausedAsync(id, paused: true);
        var reconcileReadCount = 0;
        Task<bool?> ReadForReconcileAsync(Guid torrentId, CancellationToken ct)
        {
            Interlocked.Increment(ref reconcileReadCount);
            return ReadPausedAsync(torrentId, UserId, ct);
        }

        try
        {
            await _interceptor.WaitForHeldUpdateAsync();
            Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Paused);
            Engine.Actions.Should().Equal(actionsBeforeCommand);

            var reconcile = Engine.ReconcilePausedAsync(ReadForReconcileAsync, CancellationToken.None);
            await Task.WhenAny(reconcile, Task.Delay(TimeSpan.FromMilliseconds(100)));
            reconcile.IsCompleted.Should().BeFalse();
            Volatile.Read(ref reconcileReadCount).Should().Be(0);
            Engine.Actions.Should().Equal(actionsBeforeCommand);

            _interceptor.ReleaseHeldUpdate();
            await command.WaitAsync(WaitTimeout);
            await reconcile.WaitAsync(WaitTimeout);

            managed.PausedReconcilePending.Should().BeFalse();
            Engine.Actions.Should().Equal(actionsBeforeCommand);
            Volatile.Read(ref reconcileReadCount).Should().Be(0);
            (await ReadPausedAsync(id)).Should().BeTrue();
            Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Paused);
        }
        finally
        {
            _interceptor.ReleaseHeldUpdate();
        }
    }

    [Fact]
    public async Task PauseWaitingBehindRemove_ReturnsNotFoundAfterRowIsRemoved()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        var stopEntered = NewSignal();
        var releaseStop = NewSignal();
        _interceptor.HoldPausedRead(2);
        Engine.StopHook = _ =>
        {
            stopEntered.TrySetResult();
            return releaseStop.Task;
        };

        var remove = RemoveTorrentAsync(id);
        try
        {
            await stopEntered.Task.WaitAsync(WaitTimeout);
            var pause = SetPausedAsync(id, paused: true);
            await _interceptor.WaitForPausedReadCountAsync(1);
            var heldPausedRead = _interceptor.WaitForHeldPausedReadAsync();
            await Task.WhenAny(heldPausedRead, Task.Delay(TimeSpan.FromMilliseconds(100)));
            heldPausedRead.IsCompleted.Should().BeFalse();
            pause.IsCompleted.Should().BeFalse();
            releaseStop.TrySetResult();
            await heldPausedRead;

            await remove.WaitAsync(WaitTimeout);
            _interceptor.ReleaseHeldPausedRead();
            var error = await ((Func<Task>)(async () => await pause)).Should().ThrowAsync<RpcException>();
            error.Which.StatusCode.Should().Be(StatusCode.NotFound);
        }
        finally
        {
            releaseStop.TrySetResult();
            _interceptor.ReleaseHeldPausedRead();
        }

        Engine.PauseCalls.Should().Be(0);
        Engine.StartCalls.Should().Be(0);
        Engine.Get(id).Should().BeNull();
        managed.Removing.Should().BeTrue();
    }

    [Fact]
    public async Task PartialRemoveAndMissingManager_AreInactiveWhileForeignTorrentIsNotFound()
    {
        var id = Guid.NewGuid();
        var (pendingId, managed) = await CreatePendingReconcileAsync();
        Engine.RemoveHook = async (manager, _) =>
        {
            await Engine.RemoveViaEngineAsync(manager, RemoveMode.CacheDataOnly);
            throw new IOException("remove failed after unregister");
        };
        await ((Func<Task>)(() => Engine.RemoveAsync(pendingId, deleteData: false))).Should().ThrowAsync<IOException>();
        managed.Removing.Should().BeTrue();
        var actionCount = Engine.Actions.Count;

        var pauseAfterPartialRemove = () => SetPausedAsync(pendingId, paused: true);
        (await pauseAfterPartialRemove.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        await Engine.ReconcilePausedAsync(ReadPausedForReconcileAsync, CancellationToken.None);
        managed.PausedReconcilePending.Should().BeTrue();
        Engine.Actions.Count.Should().Be(actionCount);

        var inactiveId = Guid.NewGuid();
        await SeedTorrentAsync(inactiveId, paused: false);
        var pauseWithoutManager = () => SetPausedAsync(inactiveId, paused: true);
        (await pauseWithoutManager.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);

        var foreignId = Guid.NewGuid();
        await SeedTorrentAsync(foreignId, paused: false, userId: UserId + 1);
        var pauseForeign = () => SetPausedAsync(foreignId, paused: true);
        (await pauseForeign.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task UserDeletedWaitsForPausedUpdateAndDoesNotResurrectTorrent()
    {
        var id = Guid.NewGuid();
        await SeedAndAddAsync(id, paused: false);
        _interceptor.HoldNextPausedUpdate(id);
        var pause = SetPausedAsync(id, paused: true);
        var consumeContext = new Mock<ConsumeContext<UserDeleted>>();
        consumeContext.SetupGet(context => context.Message).Returns(new UserDeleted { UserId = UserId });
        var downloadPath = Path.Combine(_root, "user-data");
        Directory.CreateDirectory(Path.Combine(downloadPath, UserId.ToString()));

        try
        {
            await _interceptor.WaitForHeldUpdateAsync();
            await using var db = CreateContext();
            var consumer = new UserDeletedConsumer(
                db,
                Engine,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Torrent:DownloadPath"] = downloadPath,
                }).Build(),
                new MetricsCollector(),
                TestLoggers.Null<UserDeletedConsumer>());
            var deleted = consumer.Consume(consumeContext.Object);
            await _interceptor.WaitForUserTorrentReadAsync();
            deleted.IsCompleted.Should().BeFalse();

            _interceptor.ReleaseHeldUpdate();
            await Task.WhenAll(pause, deleted).WaitAsync(WaitTimeout);
        }
        finally
        {
            _interceptor.ReleaseHeldUpdate();
        }

        Engine.Get(id).Should().BeNull();
        (await ReadPausedAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task PauseForOneTorrent_DoesNotWaitForAnotherTorrentUpdate()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = await SeedAndAddAsync(firstId, paused: false);
        var second = await SeedAndAddAsync(secondId, paused: false, secondTorrent: true);
        _interceptor.HoldNextPausedUpdate(firstId);
        var firstPause = SetPausedAsync(firstId, paused: true);

        try
        {
            await _interceptor.WaitForHeldUpdateAsync();
            var secondPause = SetPausedAsync(secondId, paused: true);
            await secondPause.WaitAsync(WaitTimeout);

            firstPause.IsCompleted.Should().BeFalse();
            (await ReadPausedAsync(firstId)).Should().BeFalse();
            (await ReadPausedAsync(secondId)).Should().BeTrue();
            first.PausedReconcilePending.Should().BeFalse();
            second.PausedReconcilePending.Should().BeFalse();

            _interceptor.ReleaseHeldUpdate();
            await firstPause.WaitAsync(WaitTimeout);
        }
        finally
        {
            _interceptor.ReleaseHeldUpdate();
        }

        (await ReadPausedAsync(firstId)).Should().BeTrue();
    }

    [Fact]
    public async Task CancellationWhileWaitingForGate_DoesNotRunTheSecondAction()
    {
        var id = Guid.NewGuid();
        await SeedAndAddAsync(id, paused: false);
        _interceptor.HoldNextPausedUpdate(id);
        var first = SetPausedAsync(id, paused: true);
        using var cancellation = new CancellationTokenSource();

        try
        {
            await _interceptor.WaitForHeldUpdateAsync();
            var second = SetPausedAsync(id, paused: false, cancellation.Token);
            await _interceptor.WaitForPausedReadCountAsync(3);
            await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(100)));
            second.IsCompleted.Should().BeFalse();
            Engine.Actions.Should().Equal("Pause");
            cancellation.Cancel();

            await ((Func<Task>)(async () => await second)).Should().ThrowAsync<OperationCanceledException>();
            Engine.Actions.Should().Equal("Pause");

            _interceptor.ReleaseHeldUpdate();
            await first.WaitAsync(WaitTimeout);
        }
        finally
        {
            _interceptor.ReleaseHeldUpdate();
        }

        (await ReadPausedAsync(id)).Should().BeTrue();
        Engine.LastAction.Should().Be("Pause");
    }

    [Fact]
    public async Task CancellationAfterEngineAction_StillCommitsAndKeepsEngineInSync()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        _interceptor.HoldNextPausedUpdate(id);
        using var cancellation = new CancellationTokenSource();
        var pause = SetPausedAsync(id, paused: true, cancellation.Token);

        try
        {
            await _interceptor.WaitForHeldUpdateAsync();
            Engine.LastAction.Should().Be("Pause");
            cancellation.Cancel();
            _interceptor.ReleaseHeldUpdate();

            await pause.WaitAsync(WaitTimeout);
        }
        finally
        {
            _interceptor.ReleaseHeldUpdate();
        }

        (await ReadPausedAsync(id)).Should().BeTrue();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Paused);
    }

    [Fact]
    public async Task PersistenceService_ReconcilesEvenWhenStatisticsFlushFails()
    {
        var (id, managed) = await CreatePendingReconcileAsync();
        await using (var context = CreateContext())
        {
            await context.Torrents.Where(torrent => torrent.Id == id)
                .ExecuteUpdateAsync(updates => updates.SetProperty(torrent => torrent.Progress, 2d));
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_torrent_flush BEFORE UPDATE ON Torrents
                BEGIN SELECT RAISE(ABORT, 'statistics flush blocked'); END;
                """);
        }
        var statisticsUpdatesBeforeFlush = _interceptor.StatisticsUpdateCount;

        var started = NewSignal();
        Engine.StartHook = _ =>
        {
            started.TrySetResult();
            return Task.CompletedTask;
        };
        var persistence = new TorrentPersistenceService(
            Engine,
            ScopeFactory,
            TestLoggers.Null<TorrentPersistenceService>());

        await persistence.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(WaitTimeout);
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
        }

        managed.PausedReconcilePending.Should().BeFalse();
        Engine.CurrentState(managed.Manager).Should().Be(TorrentState.Downloading);
        (await ReadPausedAsync(id)).Should().BeFalse();
        _interceptor.StatisticsUpdateCount.Should().BeGreaterThan(statisticsUpdatesBeforeFlush);
    }

    [Fact]
    public async Task Startup_RestoresEveryTorrentAndStartsOnlyUnpausedRows()
    {
        var pausedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        await SeedTorrentAsync(pausedId, paused: true, torrentFile: _harness.TorrentBytes);
        await SeedTorrentAsync(activeId, paused: false, torrentFile: _harness.SecondTorrentBytes);
        var restartEngine = new TestEngine();
        var starts = new List<Guid>();
        restartEngine.StartHook = manager =>
        {
            starts.Add(restartEngine.All.Single(item => ReferenceEquals(item.Manager, manager)).Id);
            return Task.CompletedTask;
        };
        var startup = new TorrentStartupService(
            restartEngine,
            ScopeFactory,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Torrent:DownloadPath"] = Path.Combine(_root, "restart"),
                ["Torrent:PeerPort"] = "0",
            }).Build(),
            TestLoggers.Null<TorrentStartupService>());

        try
        {
            await startup.StartAsync(CancellationToken.None);
            restartEngine.All.Select(item => item.Id).Should().BeEquivalentTo(new[] { pausedId, activeId });
            starts.Should().Equal(new[] { activeId });
        }
        finally
        {
            await restartEngine.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(TorrentState.Downloading, true, "Pause")]
    [InlineData(TorrentState.Seeding, true, "Pause")]
    [InlineData(TorrentState.Hashing, true, "Pause")]
    [InlineData(TorrentState.Metadata, true, "Stop")]
    [InlineData(TorrentState.Starting, true, "Stop")]
    [InlineData(TorrentState.Paused, true, "")]
    [InlineData(TorrentState.HashingPaused, true, "")]
    [InlineData(TorrentState.Stopped, true, "")]
    [InlineData(TorrentState.Error, true, "")]
    [InlineData(TorrentState.Stopping, true, "")]
    [InlineData(TorrentState.Stopped, false, "Start")]
    [InlineData(TorrentState.Paused, false, "Start")]
    [InlineData(TorrentState.Error, false, "Start")]
    [InlineData(TorrentState.HashingPaused, false, "Start")]
    [InlineData(TorrentState.Downloading, false, "")]
    [InlineData(TorrentState.Seeding, false, "")]
    [InlineData(TorrentState.Hashing, false, "")]
    [InlineData(TorrentState.Metadata, false, "")]
    [InlineData(TorrentState.Starting, false, "")]
    [InlineData(TorrentState.Stopping, false, "")]
    public async Task StateOf_SelectsTheEngineActionWithoutNetwork(TorrentState state, bool paused, string expectedAction)
    {
        var id = Guid.NewGuid();
        var managed = await _harness.AddAsync(id);
        Engine.SetState(managed.Manager, state);

        (await Engine.ApplyPausedForTestAsync(id, paused)).Should().BeTrue();

        if (string.IsNullOrEmpty(expectedAction))
            Engine.Actions.Should().BeEmpty();
        else
            Engine.Actions.Should().Equal(expectedAction);
    }

    private async Task<(Guid Id, TorrentEngineService.ManagedTorrent Managed)> CreatePendingReconcileAsync()
    {
        var id = Guid.NewGuid();
        var managed = await SeedAndAddAsync(id, paused: false);
        _interceptor.FailNextUpdateBefore(new IOException("update failed"));
        Engine.StartHook = _ => throw new IOException("compensation failed");

        var act = () => SetPausedAsync(id, paused: true);
        (await act.Should().ThrowAsync<IOException>()).WithMessage("update failed");
        managed.PausedReconcilePending.Should().BeTrue();
        Engine.StartHook = null;
        return (id, managed);
    }

    private async Task<TorrentEngineService.ManagedTorrent> SeedAndAddAsync(
        Guid id,
        bool paused,
        bool secondTorrent = false,
        long userId = UserId,
        double progress = 0)
    {
        await SeedTorrentAsync(id, paused, userId, progress);
        var managed = secondTorrent
            ? await _harness.AddSecondAsync(id)
            : await _harness.AddAsync(id);
        Engine.SetState(managed.Manager, paused ? TorrentState.Stopped : TorrentState.Downloading);
        return managed;
    }

    private async Task SeedTorrentAsync(
        Guid id,
        bool paused,
        long userId = UserId,
        double progress = 0,
        byte[]? torrentFile = null)
    {
        await using var context = CreateContext();
        context.Torrents.Add(new TorrentEntity
        {
            Id = id,
            UserId = userId,
            InfoHash = id.ToString("N"),
            Name = "test torrent",
            SavePath = Path.Combine(_root, "downloads", userId.ToString()),
            Status = (int)TorrentStatus.Downloading,
            TorrentFile = torrentFile,
            Paused = paused,
            Progress = progress,
            AddedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task<TorrentEmpty> SetPausedAsync(Guid id, bool paused, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        var rpcContext = new TestServerCallContext(cancellationToken: ct);
        return paused
            ? await service.PauseTorrent(IdRequest(id), rpcContext)
            : await service.ResumeTorrent(IdRequest(id), rpcContext);
    }

    private async Task RemoveTorrentAsync(Guid id)
    {
        await using var context = CreateContext();
        await CreateService(context).RemoveTorrent(
            new RemoveTorrentRequest { Id = id.ToString(), DeleteFiles = false },
            new TestServerCallContext());
    }

    private async Task DeleteTorrentAsync(Guid id)
    {
        await using var context = CreateContext();
        await context.Torrents.Where(torrent => torrent.Id == id).ExecuteDeleteAsync();
    }

    private TorrentApiService CreateService(TorrentContext context, long userId = UserId) => new(
        UserContextFactory.Create(userId),
        new TokenRevocationCache(),
        new TorrentStore(context),
        Engine,
        import: null!,
        ScopeFactory,
        new ConfigurationBuilder().Build(),
        new MetricsCollector());

    private async Task<bool?> ReadPausedAsync(Guid id, long userId = UserId, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        return await new TorrentStore(context).GetPaused(id, userId, ct);
    }

    private async Task<bool?> ReadPausedForReconcileAsync(Guid id, CancellationToken ct)
        => await ReadPausedAsync(id, UserId, ct);

    private TorrentContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TorrentContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(_interceptor)
            .Options;
        return new TorrentContext(options);
    }

    private static TorrentIdRequest IdRequest(Guid id) => new() { Id = id.ToString() };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PauseCommandInterceptor : DbCommandInterceptor
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _heldUpdateEntered;
        private TaskCompletionSource? _releaseHeldUpdate;
        private Guid? _heldUpdateId;
        private bool _heldUpdateClaimed;
        private int _heldPausedReadNumber;
        private bool _heldPausedReadClaimed;
        private TaskCompletionSource? _heldPausedReadEntered;
        private TaskCompletionSource? _releaseHeldPausedRead;
        private readonly ConcurrentDictionary<int, Exception> _pausedReadFailures = new();
        private Exception? _failBefore;
        private Func<Task>? _beforeUpdateFailure;
        private Exception? _failAfter;
        private int _pausedReadCount;
        private int _pausedReadExecutionCount;
        private int _statisticsUpdateCount;
        private int _userTorrentReadCount;
        private readonly Dictionary<int, TaskCompletionSource> _pausedReadWaiters = new();
        private TaskCompletionSource? _userTorrentReadWaiter;

        public int StatisticsUpdateCount => Volatile.Read(ref _statisticsUpdateCount);

        public void HoldNextPausedUpdate(Guid? id = null)
        {
            lock (_sync)
            {
                _heldUpdateEntered = NewSignal();
                _releaseHeldUpdate = NewSignal();
                _heldUpdateId = id;
                _heldUpdateClaimed = false;
            }
        }

        public Task WaitForHeldUpdateAsync()
        {
            lock (_sync)
                return (_heldUpdateEntered ?? throw new InvalidOperationException("No update barrier was configured"))
                    .Task.WaitAsync(WaitTimeout);
        }

        public void ReleaseHeldUpdate()
        {
            lock (_sync)
                _releaseHeldUpdate?.TrySetResult();
        }

        public void HoldPausedRead(int readNumber)
        {
            lock (_sync)
            {
                _heldPausedReadNumber = readNumber;
                _heldPausedReadEntered = NewSignal();
                _releaseHeldPausedRead = NewSignal();
                _heldPausedReadClaimed = false;
            }
        }

        public Task WaitForHeldPausedReadAsync()
        {
            lock (_sync)
                return (_heldPausedReadEntered ?? throw new InvalidOperationException("No read barrier was configured"))
                    .Task.WaitAsync(WaitTimeout);
        }

        public void ReleaseHeldPausedRead()
        {
            lock (_sync)
                _releaseHeldPausedRead?.TrySetResult();
        }

        public void FailNextUpdateBefore(Exception error, Func<Task>? beforeThrow = null)
        {
            Interlocked.Exchange(ref _failBefore, error);
            Interlocked.Exchange(ref _beforeUpdateFailure, beforeThrow);
        }

        public void FailNextUpdateAfter(Exception error) => Interlocked.Exchange(ref _failAfter, error);

        public void FailPausedRead(int readNumber, Exception error) => _pausedReadFailures[readNumber] = error;

        public Task WaitForPausedReadCountAsync(int count)
        {
            lock (_sync)
            {
                if (_pausedReadCount >= count)
                    return Task.CompletedTask;
                if (!_pausedReadWaiters.TryGetValue(count, out var waiter))
                    _pausedReadWaiters[count] = waiter = NewSignal();
                return waiter.Task.WaitAsync(WaitTimeout);
            }
        }

        public Task WaitForUserTorrentReadAsync()
        {
            lock (_sync)
            {
                if (_userTorrentReadCount > 0)
                    return Task.CompletedTask;
                return (_userTorrentReadWaiter ??= NewSignal()).Task.WaitAsync(WaitTimeout);
            }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsTorrentStatisticsUpdate(command))
                Interlocked.Increment(ref _statisticsUpdateCount);

            if (!IsPausedUpdate(command))
                return result;

            Task? release = null;
            lock (_sync)
            {
                if (_releaseHeldUpdate != null
                    && !_heldUpdateClaimed
                    && (_heldUpdateId == null || CommandContainsId(command, _heldUpdateId.Value)))
                {
                    _heldUpdateEntered!.TrySetResult();
                    release = _releaseHeldUpdate.Task;
                    _heldUpdateClaimed = true;
                }
            }

            if (release != null)
                await release.WaitAsync(cancellationToken);

            var error = Interlocked.Exchange(ref _failBefore, null);
            if (error != null)
            {
                var beforeFailure = Interlocked.Exchange(ref _beforeUpdateFailure, null);
                if (beforeFailure != null)
                    await beforeFailure();

                throw error;
            }

            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsPausedUpdate(command))
            {
                var error = Interlocked.Exchange(ref _failAfter, null);
                if (error != null)
                    throw error;
            }

            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsTorrentStatisticsUpdate(command))
                Interlocked.Increment(ref _statisticsUpdateCount);

            if (IsPausedRead(command))
            {
                var readNumber = Interlocked.Increment(ref _pausedReadExecutionCount);
                Task? release = null;
                lock (_sync)
                {
                    if (_releaseHeldPausedRead != null && !_heldPausedReadClaimed && _heldPausedReadNumber == readNumber)
                    {
                        _heldPausedReadEntered!.TrySetResult();
                        release = _releaseHeldPausedRead.Task;
                        _heldPausedReadClaimed = true;
                    }
                }

                if (release != null)
                    await release.WaitAsync(cancellationToken);

                _pausedReadFailures.TryRemove(readNumber, out var error);
                if (error != null)
                    throw error;
            }

            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (IsPausedRead(command))
                {
                    _pausedReadCount++;
                    foreach (var (target, waiter) in _pausedReadWaiters)
                        if (_pausedReadCount >= target)
                            waiter.TrySetResult();
                }

                if (IsUserTorrentListRead(command))
                {
                    _userTorrentReadCount++;
                    _userTorrentReadWaiter?.TrySetResult();
                }
            }

            return ValueTask.FromResult(result);
        }

        private static bool IsPausedUpdate(DbCommand command) =>
            command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("Paused", StringComparison.OrdinalIgnoreCase);

        private static bool IsTorrentStatisticsUpdate(DbCommand command) =>
            command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("Torrents", StringComparison.OrdinalIgnoreCase)
            && !IsPausedUpdate(command);

        private static bool IsPausedRead(DbCommand command)
        {
            var select = command.CommandText.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
            var from = command.CommandText.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
            if (select < 0 || from <= select)
                return false;

            var projection = command.CommandText[(select + "SELECT".Length)..from].TrimStart();
            return projection.StartsWith("\"t\".\"Paused\"", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUserTorrentListRead(DbCommand command) =>
            command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("\"UserId\"", StringComparison.Ordinal)
            && !command.CommandText.Contains("\"Id\" =", StringComparison.Ordinal);

        private static bool CommandContainsId(DbCommand command, Guid id) => command.Parameters
            .Cast<DbParameter>()
            .Any(parameter => Guid.TryParse(parameter.Value?.ToString(), out var value) && value == id);
    }
}

