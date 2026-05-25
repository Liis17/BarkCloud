using BarkCloud.Files.Persistence;

namespace BarkCloud.Files.Services;

/// <summary>
/// Фоновая периодическая зачистка корзины: раз в 6 часов окончательно удаляет записи,
/// у которых истёк срок хранения (PurgeAt в прошлом) — из БД, альбомов и S3 (включая превью).
/// </summary>
public class TrashCleanupService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TrashCleanupService> _logger;

    public TrashCleanupService(IServiceScopeFactory scopeFactory, ILogger<TrashCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при зачистке просроченных записей корзины");
            }

            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PurgeExpiredAsync(CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        var totalEntries = 0;
        var totalBlobs = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<CloudHierarchyStorage>();
            var purge = scope.ServiceProvider.GetRequiredService<TrashPurgeService>();

            var batch = await storage.GetExpiredTrashedEntries(now, BatchSize, stoppingToken);
            if (batch.Count == 0)
                break;

            totalBlobs += await purge.PurgeEntriesAsync(batch, stoppingToken);
            totalEntries += batch.Count;

            if (batch.Count < BatchSize)
                break;
        }

        if (totalEntries > 0)
            _logger.LogInformation(
                "Корзина: окончательно удалено {Entries} записей, {Blobs} блобов из S3", totalEntries, totalBlobs);
    }
}
