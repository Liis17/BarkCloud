namespace BarkCloud.Files.Services;

public sealed class StorageStatsWarmupService(
    IPhysicalStorageStatsProvider physical,
    IS3StorageStatsProvider s3,
    ILogger<StorageStatsWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(physical.GetStatsAsync(stoppingToken), s3.GetStatsAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Прогрев статистики хранилища не выполнен");
        }
    }
}
