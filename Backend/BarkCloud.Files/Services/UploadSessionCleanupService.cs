using BarkCloud.GrpcServer.Metrics;

namespace BarkCloud.Files.Services;

public sealed class UploadSessionCleanupService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    MetricsCollector metrics,
    ILogger<UploadSessionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<UploadSessionMaintenance>()
                    .RunOnceAsync(stoppingToken);
                metrics.Increment("upload_cleanup_runs_total");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                metrics.Increment("upload_cleanup_failures_total");
                logger.LogError(error, "Ошибка обслуживания upload-сессий");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
