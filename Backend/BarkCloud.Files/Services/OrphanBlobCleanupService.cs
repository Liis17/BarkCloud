using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

/// <summary>
/// Фоновая периодическая зачистка осиротевших блобов: раз в 6 часов находит записи
/// <see cref="Domain.UploadFile"/> с пустым списком Uploaders (на них больше никто не ссылается)
/// и физически удаляет их из S3 и БД через <see cref="TrashPurgeService.PurgeOrphanBlobsAsync"/>.
/// </summary>
/// <remarks>
/// Покрывает пути, которые лишь декрементят Uploaders, но не чистят S3 немедленно — удаление
/// аккаунта (<see cref="Consumers.UserDeletedConsumer"/>) и удаление медиа из галереи. Также
/// повторяет попытку для блобов, чьё удаление из S3 ранее не удалось.
/// </remarks>
public class OrphanBlobCleanupService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private const int BatchSize = 500;
    // Верхняя граница батчей за один проход — защита от зацикливания на блобах, которые не
    // удаляются (например, при недоступном S3).
    private const int MaxBatchesPerRun = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrphanBlobCleanupService> _logger;

    public OrphanBlobCleanupService(IServiceScopeFactory scopeFactory, ILogger<OrphanBlobCleanupService> logger)
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
                await PurgeOrphansAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при зачистке осиротевших блобов");
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

    private async Task PurgeOrphansAsync(CancellationToken stoppingToken)
    {
        var totalBlobs = 0;

        for (var i = 0; i < MaxBatchesPerRun && !stoppingToken.IsCancellationRequested; i++)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
            var purge = scope.ServiceProvider.GetRequiredService<ITrashPurgeService>();

            var batch = await context.UploadedFiles
                .AsNoTracking()
                .Where(f => f.Uploaders.Count == 0)
                .Select(f => f.Id)
                .Take(BatchSize)
                .ToListAsync(stoppingToken);
            if (batch.Count == 0)
                break;

            var purged = await purge.PurgeOrphanBlobsAsync(batch, stoppingToken);
            totalBlobs += purged;

            // Если из батча не удалён ни один блоб (например, S3 недоступен), прерываемся, чтобы не
            // крутить тот же набор до исчерпания лимита — повторим на следующем тике.
            if (purged == 0)
                break;
        }

        if (totalBlobs > 0)
            _logger.LogInformation("Осиротевших блобов удалено из S3: {Blobs}", totalBlobs);
    }
}
