using BarkCloud.GrpcServer;

using System.Diagnostics;

namespace BarkCloud.GrpcServer.Tests;

[Collection("Logging lifecycle")]
public class BarkCloudDiagnosticsTests
{
    [Fact]
    public void StartOperationWithDefaultParentCreatesRootIndependentOfAmbientActivity()
    {
        Activity? startedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BarkCloud",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            ActivityStarted = activity => startedActivity = activity
        };
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("ambient")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        var operation = BarkCloudDiagnostics.StartOperation("root-operation");
        var activity = operation.Activity;

        activity.TraceId.Should().NotBe(ambient!.TraceId);
        activity.ParentSpanId.Should().Be(default(ActivitySpanId));
        activity.Parent.Should().BeNull();
        operation.Context.TraceId.Should().Be(activity.TraceId);
        operation.Context.SpanId.Should().Be(activity.SpanId);
        startedActivity.Should().BeSameAs(activity);
        Activity.Current.Should().BeSameAs(activity);

        operation.Dispose();
        Activity.Current.Should().BeSameAs(ambient);
    }

    [Fact]
    public void StartOperationWithoutListenerStillCreatesAndRestoresIndependentRoot()
    {
        using var ambient = new Activity("ambient-without-listener")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        var operation = BarkCloudDiagnostics.StartOperation("root-without-listener");
        var activity = operation.Activity;

        activity.IdFormat.Should().Be(ActivityIdFormat.W3C);
        activity.TraceId.Should().NotBe(ambient!.TraceId);
        activity.ParentSpanId.Should().Be(default(ActivitySpanId));
        activity.Parent.Should().BeNull();
        Activity.Current.Should().BeSameAs(activity);

        operation.Dispose();
        Activity.Current.Should().BeSameAs(ambient);
    }

    [Fact]
    public void StartOperationWithRemoteUnsampledParentCreatesChildAndPreservesTraceState()
    {
        using var listener = BarkCloudDiagnostics.CreateActivityListener();
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("unrelated-ambient")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        var parent = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.None,
            "vendor=value",
            isRemote: true);

        using var operation = BarkCloudDiagnostics.StartOperation("remote-child", parent);
        var activity = operation.Activity;

        activity.TraceId.Should().Be(parent.TraceId);
        activity.ParentSpanId.Should().Be(parent.SpanId);
        activity.TraceStateString.Should().Be(parent.TraceState);
        activity.ActivityTraceFlags.Should().Be(ActivityTraceFlags.None);
        Activity.Current.Should().BeSameAs(activity);

        operation.Dispose();
        Activity.Current.Should().BeSameAs(ambient);
    }

    [Fact]
    public async Task LoggingLifetimeDisposesActivityListenerAfterLoggerFlush()
    {
        var source = new ActivitySource("MassTransit");
        var listener = BarkCloudDiagnostics.CreateActivityListener();
        ActivitySource.AddActivityListener(listener);
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new BarkCloudLoggingLifetime(false, null, async () =>
        {
            flushStarted.SetResult();
            await releaseFlush.Task;
        }, activityListener: listener);

        var disposal = lifetime.DisposeAsync().AsTask();
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var whileFlushing = source.StartActivity("while-flushing");
        whileFlushing.Should().NotBeNull();

        releaseFlush.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));

        source.StartActivity("after-flush").Should().BeNull();
        source.Dispose();
    }
}
