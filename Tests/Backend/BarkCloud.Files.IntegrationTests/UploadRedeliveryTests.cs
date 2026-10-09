using System.Diagnostics;
using System.Text;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Scheduling;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Quartz;
using Quartz.Impl.Matchers;

using RabbitMQ.Client;

using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BarkCloud.Files.IntegrationTests;

public sealed class UploadRedeliveryTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly UploadTestDatabase _database = new();
    private readonly ProcessingProbe _probe = new();
    private static readonly TimeSpan[] FastIntervals = Enumerable.Repeat(TimeSpan.FromMilliseconds(300), 4).ToArray();

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        await using var connection = await new ConnectionFactory { Uri = UploadTestHost.RabbitAddress }.CreateConnectionAsync();
        output.WriteLine("RabbitMQ actual version: {0}", Encoding.UTF8.GetString((byte[])connection.ServerProperties!["version"]!));
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in new[] { "process-uploaded-file", "process-uploaded-file_error", "files-upload-scheduler", "files-upload-scheduler_error" })
            await channel.QueueDeleteAsync(queue);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task TwoFailures_ReleaseBothSlotsBeforeFirstProductionRedelivery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new[] { await _database.AddSessionAsync(), await _database.AddSessionAsync() };
        foreach (var id in failed)
            _probe.Pipelines[id] = async (_, _, token) =>
            {
                if (Volatile.Read(ref _probe.Active) == 2) entered.TrySetResult();
                await release.Task.WaitAsync(token);
                throw new IOException("pipeline temporarily unavailable");
            };
        await using var host = new UploadTestHost(_database, _probe);
        await host.StartAsync();
        foreach (var id in failed) await host.SendAsync(id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        release.SetResult();
        var scheduler = await host.SchedulerAsync();
        await WaitUntilAsync(async () => (await TriggersAsync(scheduler)).Count == 2);
        var healthy = await _database.AddSessionAsync();
        var started = Stopwatch.GetTimestamp();
        await host.SendAsync(healthy);
        await WaitUntilAsync(async () => (await _database.ReadAsync(healthy)).Status == UploadSessionStatus.Ready, TimeSpan.FromSeconds(5));

        Stopwatch.GetElapsedTime(started).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
        failed.Select(id => _probe.Deliveries[id].ToArray()).Should().OnlyContain(counts => counts.SequenceEqual(new[] { 0 }));
        _probe.MaximumActive.Should().Be(2);
    }

    [Fact]
    public async Task FilesRestart_PreservesRedeliveryAndFiresOverdueTimer()
    {
        var id = await AddFailOnceAsync();
        await using (var first = new UploadTestHost(_database, _probe))
        {
            await first.StartAsync();
            await first.SendAsync(id);
            await WaitForPendingAsync(first);
        }
        await Task.Delay(TimeSpan.FromSeconds(12));
        // Migrate on every Files startup must preserve pending payloads.
        await using (var context = _database.CreateContext())
        {
            // An EF rollback/reapply must also retain the scheduler's pending deliveries.
            await context.GetService<IMigrator>().MigrateAsync("20261008052536_EnforceCloudFileOriginalIntegrity");
            await context.Database.MigrateAsync();
        }
        await using var restarted = new UploadTestHost(_database, _probe);
        await restarted.StartAsync();
        await WaitUntilAsync(async () => (await _database.ReadAsync(id)).Status == UploadSessionStatus.Ready);

        _probe.Deliveries[id].Should().Equal(0, 1);
        (await _database.ReadAsync(id)).ProcessingAttempts.Should().Be(2);
        await AssertNoPendingAsync(restarted);
    }

    [Fact]
    public async Task RabbitRestartDuringWait_PreservesMessageAndRedeliveryCount()
    {
        var id = await AddFailOnceAsync();
        await using var host = new UploadTestHost(_database, _probe);
        await host.StartAsync();
        await host.SendAsync(id);
        await WaitForPendingAsync(host);
        await BrokerAsync("stop");
        try { await Task.Delay(TimeSpan.FromSeconds(12)); }
        finally { await BrokerAsync("start"); }
        await WaitUntilAsync(async () => (await _database.ReadAsync(id)).Status == UploadSessionStatus.Ready);

        _probe.Deliveries[id].Should().Equal(0, 1);
        (await _database.ReadAsync(id)).ProcessingAttempts.Should().Be(2);
        await AssertNoPendingAsync(host);
    }

    [Fact]
    public async Task TerminalSendFailureWhileRabbitDown_PersistsRecoveryAcrossFilesRestart()
    {
        var id = await AddFailOnceAsync();
        await using (var host = new UploadTestHost(_database, _probe))
        {
            await host.StartAsync();
            await host.SendAsync(id);
            await WaitForPendingAsync(host);
            var scheduler = await host.SchedulerAsync();
            var original = (await TriggersAsync(scheduler)).Single();
            await BrokerAsync("stop");
            try
            {
                await WaitUntilAsync(async () => (await scheduler.GetCurrentlyExecutingJobs()).Count > 0);
                // Transport normally waits for reconnect. Interrupt its blocked send to exercise
                // the native job's bounded refires and the listener's final-error path.
                await scheduler.Interrupt(original.JobKey);
                await WaitUntilAsync(async () => (await TriggersAsync(scheduler)).Any(t => t.Key.Group == "files-transport-recovery"));
                var recovery = (await TriggersAsync(scheduler)).Single(t => t.Key.Group == "files-transport-recovery");
                recovery.JobDataMap.Should().BeEquivalentTo(original.JobDataMap);
                recovery.StartTimeUtc.Should().BeAfter(DateTimeOffset.UtcNow);
                (await _database.ReadAsync(id)).ProcessingAttempts.Should().Be(1);
                _probe.Active.Should().Be(0);
            }
            finally { await BrokerAsync("start"); }
        }
        await using var restarted = new UploadTestHost(_database, _probe);
        await restarted.StartAsync();
        await WaitUntilAsync(async () => (await _database.ReadAsync(id)).Status == UploadSessionStatus.Ready);
        _probe.Deliveries[id].Should().Equal(0, 1);
        (await _database.ReadAsync(id)).ProcessingAttempts.Should().Be(2);
        await AssertNoPendingAsync(restarted);
    }

    [Fact]
    public async Task ReadyReplay_BeforeAndAfterRestart_DoesNotRepeatPipelineOrSuccess()
    {
        var id = await _database.AddSessionAsync();
        DateTime? uploadedAt;
        await using (var first = new UploadTestHost(_database, _probe, FastIntervals))
        {
            await first.StartAsync();
            await first.SendAsync(id);
            await WaitUntilAsync(async () => (await _database.ReadAsync(id)).Status == UploadSessionStatus.Ready);
            await using var context = _database.CreateContext();
            uploadedAt = (await context.UploadedFiles.FindAsync((await _database.ReadAsync(id)).FileId))!.UploadedAt;
            await first.SendAsync(id);
            await WaitUntilAsync(() => Task.FromResult(_probe.Completed.GetValueOrDefault(id) == 2));
            await AssertNoPendingAsync(first);
        }
        await using var restarted = new UploadTestHost(_database, _probe, FastIntervals);
        await restarted.StartAsync();
        await restarted.SendAsync(id);
        await WaitUntilAsync(() => Task.FromResult(_probe.Completed.GetValueOrDefault(id) == 3));
        await AssertNoPendingAsync(restarted);

        _probe.PipelineCalls[id].Should().Be(1);
        var stored = await _database.ReadAsync(id);
        stored.ProcessingAttempts.Should().Be(1);
        await using var finalContext = _database.CreateContext();
        (await finalContext.UploadedFiles.FindAsync(stored.FileId))!.UploadedAt.Should().Be(uploadedAt);
    }

    [Fact]
    public async Task CancellationDuringShutdown_RequeuesWithoutSpendingProcessingAttempt()
    {
        var id = await _database.AddSessionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probe.Pipelines[id] = async (session, database, token) =>
        {
            if (_probe.PipelineCalls[id] == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var file = await database.UploadedFiles.SingleAsync(x => x.Id == session.FileId, token);
            file.Etag = session.CompletedEtag;
            file.Size = session.DeclaredSize;
        };
        await using (var first = new UploadTestHost(_database, _probe, FastIntervals))
        {
            await first.StartAsync();
            await first.SendAsync(id);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        (await _database.ReadAsync(id)).ProcessingAttempts.Should().Be(0);
        await using var restarted = new UploadTestHost(_database, _probe, FastIntervals);
        await restarted.StartAsync();
        await WaitUntilAsync(async () => (await _database.ReadAsync(id)).Status == UploadSessionStatus.Ready);
        (await _database.ReadAsync(id)).ProcessingAttempts.Should().Be(1);
        _probe.PipelineCalls[id].Should().Be(2);
        await AssertNoPendingAsync(restarted);
    }

    [Fact]
    public async Task IntegrityFailure_ReturnedFailed_DoesNotRedeliver()
    {
        var id = await _database.AddSessionAsync();
        _probe.Pipelines[id] = (_, _, _) => throw new FileIntegrityException("bad sha");
        await using var host = new UploadTestHost(_database, _probe, FastIntervals);
        await host.StartAsync();
        await host.SendAsync(id);
        await WaitUntilAsync(async () => (await _database.ReadAsync(id)).Status == UploadSessionStatus.Failed);
        await AssertNoPendingAsync(host);
        (await _database.ReadAsync(id)).ErrorCode.Should().Be("integrity_mismatch");
        (await _database.ReadAsync(id)).ReservedBytes.Should().Be(0);
        _probe.Deliveries[id].Should().Equal(0);
        _probe.Cleanups[id].Should().Be(1);
        _probe.Faults.Should().BeEmpty();
    }

    [Fact]
    public Task Exhaustion_WithFastTestIntervals() => AssertExhaustionAsync(FastIntervals, TimeSpan.FromSeconds(60));

    [ProductionFact]
    public Task Exhaustion_WithUnchangedProductionIntervals() =>
        AssertExhaustionAsync(UploadProcessingQueue.RedeliveryIntervals, TimeSpan.FromMinutes(25));

    private async Task AssertExhaustionAsync(IReadOnlyList<TimeSpan> intervals, TimeSpan timeout)
    {
        var pipelineFailure = await _database.AddSessionAsync();
        var unhandled = await _database.AddSessionAsync(UploadSessionStatus.Uploading);
        _probe.Pipelines[pipelineFailure] = (_, _, _) => throw new IOException("permanent pipeline error");
        await using var host = new UploadTestHost(_database, _probe, intervals);
        await host.StartAsync();
        var started = Stopwatch.GetTimestamp();
        await host.SendAsync(pipelineFailure);
        await host.SendAsync(unhandled);
        await WaitUntilAsync(async () => (await _database.ReadAsync(pipelineFailure)).Status == UploadSessionStatus.Failed
            && _probe.Faults.Any(f => f.Message.SessionId == unhandled), timeout);
        output.WriteLine("Complete retry chain elapsed: {0}", Stopwatch.GetElapsedTime(started));
        var failed = await _database.ReadAsync(pipelineFailure);
        failed.ProcessingAttempts.Should().Be(5);
        failed.ErrorCode.Should().Be("processing_retries_exhausted");
        failed.ReservedBytes.Should().Be(0);
        failed.CleanupPending.Should().BeFalse();
        _probe.Cleanups[pipelineFailure].Should().Be(1);
        _probe.Deliveries[pipelineFailure].Should().Equal(0, 1, 2, 3, 4);
        _probe.Deliveries[unhandled].Should().Equal(0, 1, 2, 3, 4);
        _probe.Faults.Should().ContainSingle(f => f.Message.SessionId == unhandled);
        await using var connection = await new ConnectionFactory { Uri = UploadTestHost.RabbitAddress }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var error = await channel.BasicGetAsync("process-uploaded-file_error", true);
        error.Should().NotBeNull();
        Encoding.UTF8.GetString(error!.Body.Span).Should().Contain(unhandled.ToString());
        (await channel.QueueDeclarePassiveAsync("process-uploaded-file_error")).MessageCount.Should().Be(0);
        await AssertNoPendingAsync(host);
    }

    private async Task<Guid> AddFailOnceAsync()
    {
        var id = await _database.AddSessionAsync();
        _probe.Pipelines[id] = async (session, database, token) =>
        {
            if (session.ProcessingAttempts == 1) throw new IOException("first attempt fails");
            var file = await database.UploadedFiles.SingleAsync(x => x.Id == session.FileId, token);
            file.Etag = session.CompletedEtag;
            file.Size = session.DeclaredSize;
        };
        return id;
    }

    private static async Task WaitForPendingAsync(UploadTestHost host)
    {
        var scheduler = await host.SchedulerAsync();
        await WaitUntilAsync(async () => (await TriggersAsync(scheduler)).Count > 0);
    }

    private static async Task AssertNoPendingAsync(UploadTestHost host)
    {
        var scheduler = await host.SchedulerAsync();
        await WaitUntilAsync(async () => (await TriggersAsync(scheduler)).Count == 0
            && (await scheduler.GetCurrentlyExecutingJobs()).Count == 0);
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await TriggersAsync(scheduler)).Should().BeEmpty();
    }

    private static async Task<List<ITrigger>> TriggersAsync(IScheduler scheduler)
    {
        var triggers = new List<ITrigger>();
        foreach (var key in await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup()))
        {
            var trigger = await scheduler.GetTrigger(key);
            if (trigger is not null) triggers.Add(trigger);
        }
        return triggers;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var started = Stopwatch.GetTimestamp();
        while (!await condition())
        {
            if (Stopwatch.GetElapsedTime(started) > (timeout ?? TimeSpan.FromSeconds(60)))
                throw new TimeoutException("Integration condition was not met.");
            await Task.Delay(100);
        }
    }

    private static async Task BrokerAsync(string action)
    {
        var container = Environment.GetEnvironmentVariable("BARKCLOUD_TEST_RABBITMQ_CONTAINER")
            ?? throw new InvalidOperationException("Use run-f18.sh: restart tests require an isolated Docker broker.");
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(action);
        start.ArgumentList.Add(container);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"docker {action}: {error}");
    }
}

public sealed class ProductionFactAttribute : FactAttribute
{
    public ProductionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BARKCLOUD_F18_PRODUCTION") != "1")
            Skip = "Set BARKCLOUD_F18_PRODUCTION=1 for the complete 21m10s production retry chain.";
    }
}
