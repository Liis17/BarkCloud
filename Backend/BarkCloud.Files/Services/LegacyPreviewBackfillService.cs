using BarkCloud.Files.Domain;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

using System.Security.Cryptography;

namespace BarkCloud.Files.Services;

/// <summary>
/// Разовый (при старте контейнера) бэкафилл превью для ранее загруженных фото-оригиналов,
/// у которых превью отсутствуют. Покрывает легаси-файлы, залитые до починки HEIC-пайплайна:
/// если оригинал в HEIC — он перекодируется в JPEG (ImageSharp HEIC не декодирует, браузеры
/// его не отображают), затем для него генерируются превью 1024/512/128.
/// </summary>
/// <remarks>
/// Запускается каждый старт, но это дёшево: как только у файла появились превью, он выпадает
/// из выборки кандидатов. Идёт по курсору <c>Id</c> по возрастанию — гарантирует продвижение
/// вперёд даже если отдельный файл не обработался (будет повторён при следующем старте).
/// Видео в бэкафилл не входят — для них превью создаётся на загрузке и при ручной смене обложки.
/// </remarks>
public class LegacyPreviewBackfillService : BackgroundService
{
    /// <summary>Задержка перед стартом — дать время миграциям БД и инициализации бакетов S3.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private const int BatchSize = 200;

    /// <summary>Ширины превью — те же, что на загрузке облачных изображений.</summary>
    private static readonly int[] CloudPreviewWidths = { 1024, 512, 128 };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegacyPreviewBackfillService> _logger;

    public LegacyPreviewBackfillService(IServiceScopeFactory scopeFactory, ILogger<LegacyPreviewBackfillService> logger)
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
            _logger.LogError(ex, "Ошибка бэкафилла превью");
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
                                && !context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                                && !context.FilePreviews.Any(p => p.OriginalFileId == f.Id))
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
                    _logger.LogError(ex, "Не удалось создать превью для файла {FileId}", id);
                }
            }
        }

        if (processed > 0 || failed > 0)
            _logger.LogInformation("Бэкафилл превью завершён: обработано {Processed}, ошибок {Failed}", processed, failed);
    }

    private async Task ProcessFileAsync(IServiceProvider sp, Guid fileId, CancellationToken ct)
    {
        var filesStorage = sp.GetRequiredService<IUploadedFilesStorage>();
        var hashesStorage = sp.GetRequiredService<IFileHashesStorage>();
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

            byte[] previewSource;
            var isHeic = file.Filename.GetContentType() == "image/heic";

            if (isHeic)
            {
                var jpeg = await heic.ConvertToJpegAsync(tempPath, ct);

                string newHash;
                using (var sha256 = SHA256.Create())
                    newHash = Convert.ToHexString(sha256.ComputeHash(jpeg)).ToLowerInvariant();

                var hashOwner = await hashesStorage.GetFileIdByHash(newHash);

                // Заменяем блоб в S3 на JPEG под тем же ключом.
                using (var ms = new MemoryStream(jpeg))
                    file.Etag = await s3.UploadAsync(bucket, file.Id.ToString(), ms, "image/jpeg");

                file.Filename = Path.ChangeExtension(file.Filename, ".jpg");
                file.Size = jpeg.Length;

                // Обновляем запись хеша только если этот JPEG ещё ни за кем не закреплён —
                // иначе нарушим уникальность Hash (редкая коллизия с уже сконвертированным дублем).
                if (hashOwner is null)
                {
                    await hashesStorage.DeleteHashByFileId(file.Id, ct);
                    await hashesStorage.AddHash(new FileHash { FileId = file.Id, Hash = newHash });
                }
                else if (hashOwner != file.Id)
                {
                    _logger.LogWarning(
                        "Хеш JPEG для {FileId} уже закреплён за {OwnerId}; запись хеша не обновлена",
                        file.Id, hashOwner);
                }

                previewSource = jpeg;
                _logger.LogInformation("Легаси-HEIC {FileId} сконвертирован в JPEG ({Size} байт)", file.Id, jpeg.Length);
            }
            else
            {
                previewSource = await File.ReadAllBytesAsync(tempPath, ct);
            }

            // Восстанавливаем размеры оригинала, если их не было (типично для старых HEIC).
            if (file.ImageWidth is null or 0)
            {
                try
                {
                    using var dimStream = new MemoryStream(previewSource);
                    var info = await compressor.ProcessImageAllInOneAsync(dimStream, false, null, ct);
                    file.ImageWidth = info.Width;
                    file.ImageHeight = info.Height;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось прочитать размеры изображения {FileId}", file.Id);
                }
            }

            await filesStorage.UpdateFile(file);

            using var previewStream = new MemoryStream(previewSource);
            var previews = await compressor.GenerateMultiplePreviewsAsync(previewStream, CloudPreviewWidths, ct);
            if (previews.Count > 0)
                await previewPersistence.PersistPreviewsAsync(file, previews, bucket, ct);
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
