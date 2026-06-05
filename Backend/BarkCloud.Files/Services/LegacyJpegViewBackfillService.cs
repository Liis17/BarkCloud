using BarkCloud.Files.Domain;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

/// <summary>
/// Разовый (при старте контейнера) бэкафилл «JpegView» — полноразмерного JPEG (90%) для просмотра —
/// для ранее загруженных фото-оригиналов, у которых его ещё нет. Покрывает легаси-файлы, залитые
/// до того, как JpegView стал генерироваться для всех изображений: в том числе старые JPEG, которые
/// раньше ссылались сами на себя и потому отдавали 404 во вьювере по прямому download-URL.
/// </summary>
/// <remarks>
/// Запускается каждый старт, но это дёшево: как только у файла появилась JpegView-связка
/// (<see cref="FilePreview"/> с <c>TargetWidth = 0</c>), он выпадает из выборки кандидатов. Идёт по
/// курсору <c>Id</c> по возрастанию — гарантирует продвижение вперёд даже если отдельный файл не
/// обработался (будет повторён при следующем старте). HEIC перекодируется через ffmpeg, прочие
/// изображения — через ImageSharp. Оригинальный блоб в S3 не трогаем — создаём отдельный JpegView-блоб
/// (его раздачу разрешает download-эндпоинт, т.к. он зарегистрирован как превью).
/// </remarks>
public class LegacyJpegViewBackfillService : BackgroundService
{
    /// <summary>Позже превью- и метаданных-бэкафиллов, чтобы не конкурировать за CPU/диск/S3 на старте.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegacyJpegViewBackfillService> _logger;

    public LegacyJpegViewBackfillService(IServiceScopeFactory scopeFactory, ILogger<LegacyJpegViewBackfillService> logger)
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
            _logger.LogError(ex, "Ошибка бэкафилла JpegView");
        }
    }

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        var cursor = Guid.Empty;
        int processed = 0, failed = 0;

        while (!ct.IsCancellationRequested)
        {
            List<Guid> batch;
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
                batch = await context.UploadedFiles
                    .AsNoTracking()
                    .Where(f => f.Type == UploadFileType.CloudFile
                                && f.MediaKind == MediaKind.Photo
                                && f.Etag != null && f.Etag != ""
                                && f.Id > cursor
                                // Сам файл не является превью-блобом (превью/JpegView другого оригинала).
                                && !context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                                // У файла ещё нет JpegView-связки.
                                && !context.FilePreviews.Any(p => p.OriginalFileId == f.Id && p.TargetWidth == 0))
                    .OrderBy(f => f.Id)
                    .Select(f => f.Id)
                    .Take(BatchSize)
                    .ToListAsync(ct);
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
                    _logger.LogError(ex, "Не удалось создать JpegView для файла {FileId}", id);
                }
            }
        }

        if (processed > 0 || failed > 0)
            _logger.LogInformation("Бэкафилл JpegView завершён: обработано {Processed}, ошибок {Failed}", processed, failed);
    }

    private async Task ProcessFileAsync(IServiceProvider sp, Guid fileId, CancellationToken ct)
    {
        var filesStorage = sp.GetRequiredService<IUploadedFilesStorage>();
        var s3 = sp.GetRequiredService<S3Uploader>();
        var bucketRegistry = sp.GetRequiredService<S3BucketRegistry>();
        var compressor = sp.GetRequiredService<ImageCompressor>();
        var heic = sp.GetRequiredService<HeicImageConverter>();
        var previewPersistence = sp.GetRequiredService<PreviewPersistenceService>();

        var file = await filesStorage.GetFile(fileId);
        if (file is null || string.IsNullOrEmpty(file.Etag))
            return;

        var bucket = bucketRegistry.GetBucketName(file.Type);

        var tempPath = Path.GetTempFileName();
        try
        {
            // Скачиваем оригинал на диск (нужно для ffmpeg при HEIC).
            await using (var s3Stream = await s3.DownloadAsync(bucket, file.Id.ToString()))
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await s3Stream.CopyToAsync(fs, ct);
            }

            byte[] jpegView;
            var isHeic = file.Filename.GetContentType() == "image/heic";

            if (isHeic)
            {
                // HEIC ImageSharp не декодирует — берём полнокадровый JPEG через ffmpeg.
                jpegView = await heic.ConvertToJpegAsync(tempPath, ct);
            }
            else
            {
                await using var src = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                jpegView = await compressor.EncodeFullJpegAsync(src, 90, ct);
            }

            var viewId = await previewPersistence.PersistJpegViewAsync(
                file, jpegView, file.ImageWidth ?? 0, file.ImageHeight ?? 0, bucket, ct);

            file.JpegViewFileId = viewId;
            await filesStorage.UpdateFile(file);

            _logger.LogInformation("JpegView создан для легаси-файла {FileId} -> {ViewId}", file.Id, viewId);
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
