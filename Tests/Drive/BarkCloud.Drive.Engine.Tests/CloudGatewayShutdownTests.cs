using System.Diagnostics;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;

using FluentAssertions;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class CloudGatewayShutdownTests
{
    [Fact]
    public async Task Bounded_flush_does_not_wait_for_an_existing_send_past_timeout()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.Invoker.Handle<DeleteFileEntriesRequest, DeleteFileEntriesResponse>(
            FakeCloudMethods.DeleteFileEntries, _ =>
            {
                sendStarted.TrySetResult();
                releaseSend.Task.GetAwaiter().GetResult();
                return new DeleteFileEntriesResponse();
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        gateway.QueueDeleteFileEntry("entry", "");

        var existingSend = Task.Factory.StartNew(() => gateway.FlushPending(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started = Stopwatch.GetTimestamp();
        var boundedFlush = Task.Factory.StartNew(
            () => gateway.FlushPending(TimeSpan.FromMilliseconds(100), throwOnFailure: true),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            var act = async () => await boundedFlush.WaitAsync(TimeSpan.FromSeconds(2));
            await act.Should().ThrowAsync<TimeoutException>();
            Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseSend.TrySetResult();
        }

        await existingSend.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Final_flush_reports_budget_skipped_deletes_without_an_attempt_count()
    {
        using var temp = new TestDirectory();
        var cloud = new FakeCloud();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.Invoker.Handle<DeleteFileEntriesRequest, DeleteFileEntriesResponse>(
            FakeCloudMethods.DeleteFileEntries, _ =>
            {
                sendStarted.TrySetResult();
                releaseSend.Task.GetAwaiter().GetResult();
                time.Advance(TimeSpan.FromSeconds(21));
                return new DeleteFileEntriesResponse();
            });
        using var gateway = cloud.CreateGateway(temp.Path("cache"), time);
        var errors = new List<string>();
        gateway.SyncError = errors.Add;
        gateway.QueueDeleteFileEntry("sent-entry", "");

        var flush = Task.Factory.StartNew(
            () => gateway.FlushPending(TimeSpan.FromSeconds(20), final: true),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++)
            gateway.QueueDeleteFileEntry($"unsent-entry-{i}", "");
        releaseSend.TrySetResult();

        await flush.WaitAsync(TimeSpan.FromSeconds(5));

        errors.Should().HaveCount(100).And.OnlyContain(error => error == Loc.T("Eng_DeleteFlushTimeout"));
    }
}
