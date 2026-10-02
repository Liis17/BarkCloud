using BarkCloud.GrpcServer.XAuth;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using System.Threading.Channels;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class RevocationSyncServiceTests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly SessionRevocation Revoked = new(42, "d1", Now.AddSeconds(-5), Now.AddHours(1));

    private static RevocationSyncService CreateSut(TokenRevocationCache cache, IRevocationFeed feed, TimeProvider? clock = null)
        => new(cache, feed, NullLogger<RevocationSyncService>.Instance, clock);

    [Fact]
    public async Task Start_LoadsFullSnapshotBeforeCompletingAndRestartRejectsOldJwt()
    {
        var pending = new TaskCompletionSource<RevocationBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new Mock<IRevocationFeed>();
        feed.Setup(x => x.FetchAsync(null, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var cache = new TokenRevocationCache();
        using var sut = CreateSut(cache, feed.Object);

        var start = sut.StartAsync(default);
        start.IsCompleted.Should().BeFalse();
        pending.SetResult(new RevocationBatch([Revoked], Now));
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        cache.IsRevoked(42, "d1", Now.AddMinutes(-1)).Should().BeTrue();
        await sut.StopAsync(default);
    }

    [Fact]
    public async Task Host_KestrelDoesNotListenUntilSnapshotIsLoaded()
    {
        var pending = new TaskCompletionSource<RevocationBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new Mock<IRevocationFeed>();
        feed.Setup(x => x.FetchAsync(null, It.IsAny<CancellationToken>())).Returns(() =>
        {
            requested.SetResult();
            return pending.Task;
        });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<TokenRevocationCache>();
        builder.Services.AddSingleton(feed.Object);
        builder.Services.AddHostedService<RevocationSyncService>();
        await using var app = builder.Build();

        var start = app.StartAsync();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Should().BeEmpty();
        start.IsCompleted.Should().BeFalse();

        pending.SetResult(new RevocationBatch([Revoked], Now));
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        app.Services.GetRequiredService<TokenRevocationCache>()
            .IsRevoked(42, "d1", Now.AddMinutes(-1)).Should().BeTrue();
        await app.StopAsync();
    }

    [Fact]
    public async Task Start_RetriesWithBackoffAndOnlyRequestsFullSnapshots()
    {
        var feed = new Mock<IRevocationFeed>();
        feed.SetupSequence(x => x.FetchAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("offline"))
            .ThrowsAsync(new IOException("offline"))
            .ReturnsAsync(new RevocationBatch([Revoked], Now));
        var clock = new TestClock();
        var cache = new TokenRevocationCache();
        using var sut = CreateSut(cache, feed.Object, clock);

        var start = sut.StartAsync(default);
        var firstDelay = await clock.NextDelay();
        firstDelay.Should().Be(TimeSpan.FromSeconds(1));
        clock.Advance(firstDelay);
        var secondDelay = await clock.NextDelay();
        secondDelay.Should().Be(TimeSpan.FromSeconds(2));
        clock.Advance(secondDelay);
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        feed.Verify(x => x.FetchAsync(null, It.IsAny<CancellationToken>()), Times.Exactly(3));
        cache.IsRevoked(42, "d1", Now.AddMinutes(-1)).Should().BeTrue();
        await sut.StopAsync(default);
    }

    [Fact]
    public async Task Start_AfterRetryLimit_FailsInsteadOfStartingWithEmptyCache()
    {
        var feed = new Mock<IRevocationFeed>();
        feed.Setup(x => x.FetchAsync(null, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));
        var clock = new TestClock();
        using var sut = CreateSut(new TokenRevocationCache(), feed.Object, clock);

        var start = sut.StartAsync(default);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var delay = await clock.NextDelay();
            delay.Should().Be(TimeSpan.FromSeconds(1 << attempt));
            clock.Advance(delay);
        }

        Func<Task> action = () => start;
        await action.Should().ThrowAsync<IOException>();
        feed.Verify(x => x.FetchAsync(null, It.IsAny<CancellationToken>()), Times.Exactly(6));
    }

    [Fact]
    public async Task Start_Cancellation_StopsWaitingWithoutRetry()
    {
        var feed = new Mock<IRevocationFeed>();
        feed.Setup(x => x.FetchAsync(null, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));
        var clock = new TestClock();
        using var cancellation = new CancellationTokenSource();
        using var sut = CreateSut(new TokenRevocationCache(), feed.Object, clock);
        var start = sut.StartAsync(cancellation.Token);
        await clock.NextDelay();

        cancellation.Cancel();

        Func<Task> action = () => start;
        await action.Should().ThrowAsync<OperationCanceledException>();
        feed.Verify(x => x.FetchAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Loop_UsesServerTimeWithOneMinuteOverlapAndPreservesCacheAndWatermarkOnFailure()
    {
        var feed = new Mock<IRevocationFeed>();
        feed.Setup(x => x.FetchAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevocationBatch([Revoked], Now));
        feed.SetupSequence(x => x.FetchAsync(Now.AddMinutes(-1), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("offline"))
            .ReturnsAsync(new RevocationBatch([], Now.AddSeconds(10)));
        feed.Setup(x => x.FetchAsync(Now.AddSeconds(10).AddMinutes(-1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevocationBatch([], Now.AddSeconds(15)));
        var clock = new TestClock();
        var cache = new TokenRevocationCache();
        using var sut = CreateSut(cache, feed.Object, clock);
        await sut.StartAsync(default);

        var delay = await clock.NextDelay();
        delay.Should().Be(TimeSpan.FromSeconds(5));
        clock.Advance(delay);
        delay = await clock.NextDelay();
        cache.IsRevoked(42, "d1", Now.AddMinutes(-1)).Should().BeTrue();
        clock.Advance(delay);
        delay = await clock.NextDelay();
        clock.Advance(delay);
        await clock.NextDelay();

        feed.Verify(x => x.FetchAsync(Now.AddMinutes(-1), It.IsAny<CancellationToken>()), Times.Exactly(2));
        feed.Verify(x => x.FetchAsync(Now.AddSeconds(10).AddMinutes(-1), It.IsAny<CancellationToken>()), Times.Once);
        cache.IsRevoked(42, "d1", Now.AddMinutes(-1)).Should().BeTrue();
        await sut.StopAsync(default);
    }

    [Fact]
    public async Task Loop_FullResyncRecoversRevocationCommittedAfterOverlap()
    {
        var clock = new TestClock();
        clock.SetUtcNow(Now);
        var feed = new CommitAwareFeed(clock);
        var cache = new TokenRevocationCache();
        using var sut = CreateSut(cache, feed, clock);
        await sut.StartAsync(default);
        await clock.NextDelay();
        await Tick(clock, 2);

        // Транзакция отзыва стартовала 2 минуты назад и закоммитилась только сейчас.
        var revokedAt = clock.GetUtcNow().UtcDateTime.AddMinutes(-2);
        feed.Commit(new SessionRevocation(42, "d1", revokedAt, revokedAt.AddHours(1)));
        await Tick(clock, 9);

        cache.IsRevoked(42, "d1", revokedAt.AddSeconds(-1)).Should().BeFalse("incremental-опрос пропускает запись старше overlap");
        feed.Requests.Count(x => x is null).Should().Be(1);

        await Tick(clock, 1);

        cache.IsRevoked(42, "d1", revokedAt.AddSeconds(-1)).Should().BeTrue();
        feed.Requests.Count(x => x is null).Should().Be(2);
        await sut.StopAsync(default);
    }

    [Fact]
    public async Task Loop_FailedFullResync_IsRetriedOnNextTick()
    {
        var feed = new Mock<IRevocationFeed>();
        feed.SetupSequence(x => x.FetchAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevocationBatch([], Now))
            .ThrowsAsync(new IOException("offline"))
            .ReturnsAsync(new RevocationBatch([Revoked], Now));
        feed.Setup(x => x.FetchAsync(It.Is<DateTime?>(since => since.HasValue), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevocationBatch([], Now));
        var clock = new TestClock();
        var cache = new TokenRevocationCache();
        using var sut = CreateSut(cache, feed.Object, clock);
        await sut.StartAsync(default);
        await clock.NextDelay();

        await Tick(clock, 11);
        feed.Verify(x => x.FetchAsync(null, It.IsAny<CancellationToken>()), Times.Once);

        await Tick(clock, 1);
        feed.Verify(x => x.FetchAsync(null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        cache.IsRevoked(42, "d1", Now.AddMinutes(-1)).Should().BeFalse();

        await Tick(clock, 1);
        feed.Verify(x => x.FetchAsync(null, It.IsAny<CancellationToken>()), Times.Exactly(3));
        cache.IsRevoked(42, "d1", Now.AddMinutes(-1)).Should().BeTrue();
        await sut.StopAsync(default);
    }

    [Fact]
    public async Task TwoReplicas_ConvergeAndDelayedRevocationDoesNotRejectNewLogin()
    {
        IReadOnlyList<SessionRevocation> sessions = [];
        var feed = new Mock<IRevocationFeed>();
        feed.Setup(x => x.FetchAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new RevocationBatch(sessions, Now)));
        var firstCache = new TokenRevocationCache();
        var secondCache = new TokenRevocationCache();
        var firstClock = new TestClock();
        var secondClock = new TestClock();
        using var first = CreateSut(firstCache, feed.Object, firstClock);
        using var second = CreateSut(secondCache, feed.Object, secondClock);
        await first.StartAsync(default);
        await second.StartAsync(default);
        sessions = [Revoked];

        firstClock.Advance(await firstClock.NextDelay());
        secondClock.Advance(await secondClock.NextDelay());
        await firstClock.NextDelay();
        await secondClock.NextDelay();

        foreach (var cache in new[] { firstCache, secondCache })
        {
            cache.IsRevoked(42, "d1", Revoked.RevokedAt.AddSeconds(-1)).Should().BeTrue();
            cache.IsRevoked(42, "d1", Revoked.RevokedAt.AddSeconds(2)).Should().BeFalse();
        }
        await first.StopAsync(default);
        await second.StopAsync(default);
    }

    [Fact]
    public async Task Snapshot_CleansExpiredCacheEntries()
    {
        var cache = new TokenRevocationCache();
        cache.Revoke(1, "expired", Now.AddHours(-2), Now.AddMinutes(-1));
        var feed = new Mock<IRevocationFeed>();
        feed.Setup(x => x.FetchAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(new RevocationBatch([], Now));
        using var sut = CreateSut(cache, feed.Object);

        await sut.StartAsync(default);

        cache.IsRevoked(1, "expired", Now.AddHours(-3)).Should().BeFalse();
        await sut.StopAsync(default);
    }

    // Каждый тик: сдвиг на интервал опроса и ожидание, пока цикл обработает ответ и снова встанет в ожидание.
    private static async Task Tick(TestClock clock, int count)
    {
        for (var i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.NextDelay();
        }
    }

    /// <summary>Повторяет фильтрацию <c>DbRevocationFeed</c>: видны только закоммиченные записи, incremental — по <c>RevokedAt</c>.</summary>
    private sealed class CommitAwareFeed(TimeProvider clock) : IRevocationFeed
    {
        private readonly List<SessionRevocation> _committed = [];

        public List<DateTime?> Requests { get; } = [];

        public void Commit(SessionRevocation session) => _committed.Add(session);

        public Task<RevocationBatch> FetchAsync(DateTime? changedSince, CancellationToken cancellationToken)
        {
            Requests.Add(changedSince);
            var serverTime = clock.GetUtcNow().UtcDateTime;
            var sessions = _committed
                .Where(x => x.ExpiresAt > serverTime && (changedSince is null || x.RevokedAt >= changedSince))
                .ToList();
            return Task.FromResult(new RevocationBatch(sessions, serverTime));
        }
    }

    private sealed class TestClock : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task<TimeSpan> NextDelay()
            => await _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
