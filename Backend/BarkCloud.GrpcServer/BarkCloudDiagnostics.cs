using System.Diagnostics;

namespace BarkCloud.GrpcServer;

public static class BarkCloudDiagnostics
{
    private const string ActivitySourceName = "BarkCloud";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static BarkCloudOperationScope StartOperation(string name, ActivityContext parentContext = default)
    {
        var previousActivity = Activity.Current;
        if (parentContext == default)
            Activity.Current = null;

        try
        {
            var activity = ActivitySource.StartActivity(name, ActivityKind.Internal, parentContext)
                ?? CreateActivity(name, parentContext);
            return new BarkCloudOperationScope(activity, previousActivity);
        }
        catch
        {
            Activity.Current = previousActivity;
            throw;
        }
    }

    internal static ActivityListener CreateActivityListener()
        => new()
        {
            ShouldListenTo = source =>
                source.Name == ActivitySourceName
                || source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || source.Name == "System.Net.Http"
                || source.Name == "Grpc.Net.Client"
                || source.Name == "MassTransit",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.PropagationData
        };

    private static Activity CreateActivity(string name, ActivityContext parentContext)
    {
        var activity = new Activity(name).SetIdFormat(ActivityIdFormat.W3C);
        if (parentContext != default)
        {
            activity.SetParentId(parentContext.TraceId, parentContext.SpanId, parentContext.TraceFlags);
            activity.TraceStateString = parentContext.TraceState;
        }

        return activity.Start()!;
    }
}

public sealed class BarkCloudOperationScope : IDisposable
{
    private readonly Activity? _previousActivity;
    private int _disposed;

    internal BarkCloudOperationScope(Activity activity, Activity? previousActivity)
    {
        Activity = activity;
        _previousActivity = previousActivity;
    }

    public Activity Activity { get; }

    public ActivityContext Context => Activity.Context;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            Activity.Stop();
        }
        finally
        {
            Activity.Current = _previousActivity;
        }
    }
}
