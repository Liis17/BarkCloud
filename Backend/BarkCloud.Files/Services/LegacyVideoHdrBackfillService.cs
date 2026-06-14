using BarkCloud.Files.Helpers;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

namespace BarkCloud.Files.Services;

/// <summary>
/// Разовый (при старте контейнера) бэкафилл HDR-признака для уже загруженных видео,
/// у которых метаданные есть, но <see cref="Domain.FileMetadata.IsHdr"/> ещё не зондировался
/// (null — записи до добавления признака). Переобрабатывает только видео: качает оригинал из S3,
/// прогоняет ffprobe, выводит HDR из color_transfer и проставляет признак (true/false — всегда).
/// </summary>
/// <remarks>
/// Идёт по курсору <c>UploadFile.Id</c> по возрастанию. Признак выставляется всегда (даже false),
/// поэтому файл выпадает из выборки и проход сходится. Стартует позже метаданных-бэкафилла,
/// чтобы не конкурировать за CPU/диск.
/// </remarks>
public class LegacyVideoHdrBackfillService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegacyVideoHdrBackfillService> _logger;

    public LegacyVideoHdrBackfillService(IServiceScopeFactory scopeFactory, ILogger<LegacyVideoHdrBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await RunBackfillAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка HDR-бэкафилла видео");
        }
    }

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        Guid? cursor = null;
        int processed = 0, failed = 0;

        while (!ct.IsCancellationRequested)
        {
            List<Guid> batch;
            using (var scope = _scopeFactory.CreateScope())
            {
                var metadataStorage = scope.ServiceProvider.GetRequiredService<IFileMetadataStorage>();
                batch = await metadataStorage.ListVideosMissingHdr(cursor, BatchSize, ct);
            }

            if (batch.Count == 0)
                break;

            foreach (var id in batch)
            {
                ct.ThrowIfCancellationRequested();
                cursor = id;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await ProcessFileAsync(scope.ServiceProvider, id, ct);
                    processed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Не удалось определить HDR для видео {FileId}", id);
                }
            }
        }

        if (processed > 0 || failed > 0)
            _logger.LogInformation(
                "HDR-бэкафилл видео завершён: обработано {Processed}, ошибок {Failed}",
                processed, failed);
    }

    private async Task ProcessFileAsync(IServiceProvider sp, Guid fileId, CancellationToken ct)
    {
        var filesStorage = sp.GetRequiredService<IUploadedFilesStorage>();
        var metadataStorage = sp.GetRequiredService<IFileMetadataStorage>();
        var s3 = sp.GetRequiredService<S3Uploader>();
        var bucketRegistry = sp.GetRequiredService<S3BucketRegistry>();
        var videoProbe = sp.GetRequiredService<VideoThumbnailExtractor>();

        var file = await filesStorage.GetFile(fileId);
        if (file is null || string.IsNullOrEmpty(file.Etag))
            return;

        var bucket = bucketRegistry.GetBucketName(file.Type);
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var s3Stream = await s3.DownloadAsync(bucket, file.Id.ToString()))
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await s3Stream.CopyToAsync(fs, ct);
            }

            var probe = await videoProbe.ProbeFullAsync(tempPath, ct);
            await metadataStorage.SetHdr(file.Id, VideoHdr.IsHdr(probe.ColorTransfer), ct);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить временный файл {TempPath}", tempPath);
            }
        }
    }
}
