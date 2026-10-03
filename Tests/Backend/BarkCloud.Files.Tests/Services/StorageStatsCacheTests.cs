using BarkCloud.Files.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public class StorageStatsCacheTests
{
    [Fact]
    public async Task ColdSnapshot_DoesNotWaitForTenSecondScan_AndCoalescesRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = Create(async token =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            return await result.Task.WaitAsync(token);
        });

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => cache.GetAsync(true, default))).WaitAsync(TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        snapshots.Should().OnlyContain(snapshot => snapshot.State == "loading" && snapshot.Value == null && snapshot.UpdatedAt == null);
        calls.Should().Be(1);
        var legacy = cache.GetAsync(false, default);
        legacy.IsCompleted.Should().BeFalse();

        // Завершаем контролируемый медленный обход без десятисекундного ожидания в тестах.
        result.SetResult(new Reading(42));
        (await legacy).Value!.Bytes.Should().Be(42);
        (await cache.GetAsync(true, default)).State.Should().Be("ready");
    }

    [Fact]
    public async Task ExpiredCache_ReturnsLastMeasurementWhileOneRefreshRuns()
    {
        var time = new MutableTimeProvider();
        var refresh = new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = Create(_ => Interlocked.Increment(ref calls) == 1
            ? Task.FromResult(new Reading(17)) : refresh.Task, time);
        var initial = await cache.GetAsync(false, default);
        time.Advance(TimeSpan.FromMinutes(6));

        var stale = await cache.GetAsync(true, default).WaitAsync(TimeSpan.FromSeconds(1));
        stale.State.Should().Be("refreshing");
        stale.Value.Should().Be(initial.Value);
        stale.UpdatedAt.Should().Be(initial.UpdatedAt);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetAsync(true, default)));
        refresh.SetResult(new Reading(23));

        var updated = await cache.GetAsync(false, default);
        updated.Value!.Bytes.Should().Be(23);
        updated.UpdatedAt.Should().Be(time.GetUtcNow());
        calls.Should().Be(2);
    }

    [Fact]
    public async Task FailedRefresh_RetainsSuccessfulMeasurementAndTimestamp()
    {
        var time = new MutableTimeProvider();
        var calls = 0;
        var cache = Create(_ => Interlocked.Increment(ref calls) == 1
            ? Task.FromResult(new Reading(17)) : Task.FromException<Reading>(new IOException("offline")), time);
        var initial = await cache.GetAsync(false, default);
        time.Advance(TimeSpan.FromMinutes(6));

        var failed = await cache.GetAsync(false, default);
        failed.State.Should().Be("error");
        failed.Value.Should().Be(initial.Value);
        failed.UpdatedAt.Should().Be(initial.UpdatedAt);
        await cache.GetAsync(true, default);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task HostShutdown_CancelsScanAndDoesNotPublishItsResult()
    {
        using var stopping = new CancellationTokenSource();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = Create(token => { started.SetResult(token); return result.Task; }, stoppingToken: stopping.Token);
        await cache.GetAsync(true, default);
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        stopping.Cancel();
        result.SetResult(new Reading(42));

        var snapshot = await cache.GetAsync(false, default);
        token.IsCancellationRequested.Should().BeTrue();
        snapshot.Value.Should().BeNull();
        snapshot.UpdatedAt.Should().BeNull();
    }

    private static StorageStatsCache<Reading> Create(Func<CancellationToken, Task<Reading>> read,
        TimeProvider? time = null, CancellationToken stoppingToken = default) =>
        new(read, time ?? TimeProvider.System, NullLogger.Instance, stoppingToken);

    private sealed record Reading(long Bytes);
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
