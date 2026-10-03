using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

public sealed class FilePlaceholderBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<FilePlaceholderBackfillService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunPassAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ошибка фонового прохода цветов превью");
                }

                await Task.Delay(TimeSpan.FromMinutes(5), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task RunPassAsync(CancellationToken cancellationToken = default)
    {
        Guid? cursor = null;
        var processed = 0;
        var failed = 0;
        var skipped = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Guid> batch;
            using (var scope = scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
                var query = context.UploadedFiles.AsNoTracking().WhereReady()
                    .Where(f => f.Type == UploadFileType.CloudFile
                        && (f.MediaKind == MediaKind.Photo || f.MediaKind == MediaKind.Video)
                        && !context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                        && !context.FilePlaceholders.Any(p => p.FileId == f.Id));
                if (cursor.HasValue)
                    query = query.Where(f => f.Id > cursor.Value);
                batch = await query.OrderBy(f => f.Id).Select(f => f.Id).Take(BatchSize)
                    .ToListAsync(cancellationToken);
            }

            if (batch.Count == 0)
                break;

            foreach (var id in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = id;
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
                    var file = await context.UploadedFiles.AsNoTracking().WhereReady()
                        .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
                    if (file is not null && await scope.ServiceProvider.GetRequiredService<FilePlaceholderService>()
                        .EnsureAsync(file, cancellationToken: cancellationToken))
                        processed++;
                    else
                        skipped++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogWarning(ex, "Не удалось получить цвета превью файла {FileId}", id);
                }
            }
        }

        if (processed + failed + skipped > 0)
            logger.LogInformation("Цвета превью: сохранено {Processed}, пропущено {Skipped}, ошибок {Failed}",
                processed, skipped, failed);
    }
}
