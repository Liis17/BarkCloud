using BarkCloud.Files.Domain;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

namespace BarkCloud.Files.Services;

/// <summary>
/// Разовый (при старте контейнера) бэкафилл <see cref="FileMetadata"/> для ранее загруженных
/// блобов, у которых метаданные ещё не извлечены. Покрывает легаси-файлы, залитые до того,
/// как метаданные стали извлекаться на пайплайне загрузки.
/// </summary>
/// <remarks>
/// Запускается каждый старт, но это дёшево: как только у файла появилась запись в
/// <c>FileMetadata</c>, он выпадает из выборки кандидатов. Идёт по курсору <c>UploadFile.Id</c>
/// по возрастанию — гарантирует продвижение вперёд даже если отдельный файл не обработался.
/// Скачивает оригинал из S3 во временный файл (нужно для ffprobe), затем стримит в нужный
/// extractor по content-type/media-kind. Аватары пропускаем — их метаданные не показываем.
/// </remarks>
public class LegacyMetadataBackfillService : BackgroundService
{
    /// <summary>Чуть позже превью-бэкафилла, чтобы не конкурировать за CPU/диск на старте.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegacyMetadataBackfillService> _logger;

    public LegacyMetadataBackfillService(IServiceScopeFactory scopeFactory, ILogger<LegacyMetadataBackfillService> logger)
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
            _logger.LogError(ex, "Ошибка бэкафилла метаданных");
        }
    }

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        Guid? cursor = null;
        int processed = 0, failed = 0, skipped = 0;

        while (!ct.IsCancellationRequested)
        {
            List<Guid> batch;
            using (var scope = _scopeFactory.CreateScope())
            {
                var metadataStorage = scope.ServiceProvider.GetRequiredService<IFileMetadataStorage>();
                batch = await metadataStorage.ListFilesMissingMetadata(cursor, BatchSize, ct);
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
                    var result = await ProcessFileAsync(scope.ServiceProvider, id, ct);
                    if (result)
                        processed++;
                    else
                        skipped++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Не удалось извлечь метаданные для файла {FileId}", id);
                }
            }
        }

        if (processed > 0 || failed > 0 || skipped > 0)
            _logger.LogInformation(
                "Бэкафилл метаданных завершён: сохранено {Processed}, пропущено {Skipped} (без метаданных), ошибок {Failed}",
                processed, skipped, failed);
    }

    /// <returns>true — метаданные извлечены и сохранены; false — для файла нечего извлекать.</returns>
    private async Task<bool> ProcessFileAsync(IServiceProvider sp, Guid fileId, CancellationToken ct)
    {
        var filesStorage = sp.GetRequiredService<IUploadedFilesStorage>();
        var metadataStorage = sp.GetRequiredService<IFileMetadataStorage>();
        var s3 = sp.GetRequiredService<S3Uploader>();
        var bucketRegistry = sp.GetRequiredService<S3BucketRegistry>();
        var extractor = sp.GetRequiredService<FileMetadataExtractor>();
        var audioExtractor = sp.GetRequiredService<AudioMetadataExtractor>();
        var videoProbe = sp.GetRequiredService<VideoThumbnailExtractor>();

        var file = await filesStorage.GetFile(fileId);
        if (file is null || string.IsNullOrEmpty(file.Etag))
            return false;
        // Аватары не показываются в свойствах файлов — метаданные для них не нужны.
        if (file.Type != UploadFileType.CloudFile)
            return false;

        var contentType = (file.Filename ?? "").GetContentType();
        var bucket = bucketRegistry.GetBucketName(file.Type);

        var isVideo = contentType.StartsWith("video/");
        var isImage = contentType.StartsWith("image/");
        var isAudio = contentType.StartsWith("audio/") || file.MediaKind == MediaKind.Audio;
        var isPdf = contentType == "application/pdf";
        var isOffice = contentType.StartsWith("application/vnd.openxmlformats-officedocument.");

        if (!isVideo && !isImage && !isAudio && !isPdf && !isOffice)
            return false;

        FileMetadata? extracted = null;
        var tempPath = Path.GetTempFileName();
        try
        {
            // Скачиваем оригинал на диск; видео обязательно нужно как файл (для ffprobe).
            await using (var s3Stream = await s3.DownloadAsync(bucket, file.Id.ToString()))
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await s3Stream.CopyToAsync(fs, ct);
            }

            if (isVideo)
            {
                var probe = await videoProbe.ProbeFullAsync(tempPath, ct);
                extracted = extractor.ExtractFromVideo(probe);
            }
            else if (isImage)
            {
                await using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                extracted = extractor.ExtractFromImage(fs);
            }
            else if (isAudio)
            {
                var probe = await audioExtractor.ProbeAsync(tempPath, ct);
                extracted = audioExtractor.ExtractMetadata(probe);
            }
            else if (isPdf)
            {
                await using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                extracted = extractor.ExtractFromPdf(fs);
            }
            else if (isOffice)
            {
                await using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                extracted = extractor.ExtractFromOffice(fs, contentType);
            }
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

        if (extracted is null)
            return false;

        extracted.FileId = file.Id;
        extracted.CreatedAt = DateTime.UtcNow;
        await metadataStorage.AddIfMissing(extracted, ct);
        return true;
    }
}
