using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;

using MediatR;

using System.Security.Cryptography;

using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Features.UploadFile;

public class UploadFileCommandHandler : IRequestHandler<UploadFileCommand, string>
{
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly IFileHashesStorage _hashesStorage;
    private readonly S3Uploader _s3Uploader;
    private readonly S3BucketRegistry _bucketRegistry;
    private readonly ImageCompressor _imageCompressor;
    private readonly VideoThumbnailExtractor _videoThumbnailExtractor;
    private readonly HeicImageConverter _heicConverter;
    private readonly PreviewPersistenceService _previewPersistence;
    private readonly ILogger<UploadFileCommandHandler> _logger;

    /// <summary>
    /// Ширины превью для облачных изображений (по убыванию — самое крупное первым).
    /// </summary>
    private static readonly int[] CloudPreviewWidths = { 1024, 512, 128 };

    /// <summary>
    /// Типы файлов, для которых нужно извлекать размеры изображения.
    /// </summary>
    private static readonly HashSet<UploadFileType> ImageTypesForDimensions =
    [
        UploadFileType.UserAvatar,
        UploadFileType.CloudFile,
    ];

    public UploadFileCommandHandler(
        IUploadedFilesStorage filesStorage,
        IFileHashesStorage hashesStorage,
        S3Uploader s3Uploader,
        S3BucketRegistry bucketRegistry,
        ImageCompressor imageCompressor,
        VideoThumbnailExtractor videoThumbnailExtractor,
        HeicImageConverter heicConverter,
        PreviewPersistenceService previewPersistence,
        ILogger<UploadFileCommandHandler> logger)
    {
        _filesStorage = filesStorage;
        _hashesStorage = hashesStorage;
        _s3Uploader = s3Uploader;
        _bucketRegistry = bucketRegistry;
        _imageCompressor = imageCompressor;
        _videoThumbnailExtractor = videoThumbnailExtractor;
        _heicConverter = heicConverter;
        _previewPersistence = previewPersistence;
        _logger = logger;
    }

    public async Task<string> Handle(UploadFileCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Начало обработки загрузки файла с ID: {FileId}", request.FileId);

        var file = await _filesStorage.GetFile(request.FileId);

        if (file is null)
        {
            _logger.LogError("Файл с ID {FileId} не найден", request.FileId);
            throw new BarkCloud.Shared.Exceptions.Files.FileNotFoundException();
        }

        // Проверяем, не был ли файл уже загружен
        if (!string.IsNullOrEmpty(file.Etag))
        {
            _logger.LogWarning("Файл с ID {FileId} уже был загружен (Etag: {Etag})", request.FileId, file.Etag);
            throw new FileAlreadyUploadedException("Файл уже был загружен");
        }

        file.Filename = request.FileName;

        // Определяем тип контента по расширению файла
        var contentType = request.FileName.GetContentType();

        // Классифицируем медиа (фото / видео / документ / аудио) для галереи и альбомов
        file.MediaKind = request.FileName.GetMediaKind();

        // Получаем имя бакета в зависимости от типа файла
        var bucketName = _bucketRegistry.GetBucketName(file.Type);

        _logger.LogInformation("Загрузка файла {FileName} с типом {ContentType} в бакет {BucketName}",
            request.FileName, contentType, bucketName);

        long fileSize = request.FileSize > 0 ? request.FileSize : request.FileStream.Length;

        var isImageType = file.Type == UploadFileType.UserAvatar;
        var isVideoContent = contentType.StartsWith("video/");
        // HEIC/HEIF ImageSharp не декодирует — перекодируем в JPEG через ffmpeg (по файлу на диске).
        var isHeic = contentType == "image/heic";

        Stream originalStream;
        // Путь к временному файлу на диске. Нужен для видео и HEIC (FFmpeg читает файл по пути),
        // а также используется для буферизации больших не-картинок. null = буфер в памяти.
        string? tempFilePath = null;

        // Видео и HEIC всегда кладём на диск (FFmpeg работает с файлом), как и большие не-картинки.
        if (isVideoContent || isHeic || (!isImageType && fileSize > 100 * 1024 * 1024))
        {
            tempFilePath = Path.GetTempFileName();
            _logger.LogInformation("Файл {FileId} ({Size} МБ) буферизуется через диск", request.FileId, fileSize / 1024 / 1024);
            // FileShare.Read — чтобы процесс ffmpeg/ffprobe мог открыть файл параллельно нашему стриму.
            var tempStream = new FileStream(
                tempFilePath, FileMode.Create, FileAccess.ReadWrite,
                FileShare.Read, 81920, FileOptions.None);
            await request.FileStream.CopyToAsync(tempStream, cancellationToken);
            tempStream.Position = 0;
            originalStream = tempStream;
        }
        else
        {
            var memStream = new MemoryStream();
            await request.FileStream.CopyToAsync(memStream, cancellationToken);
            memStream.Position = 0;
            originalStream = memStream;
        }

        void CleanupTempFile()
        {
            if (tempFilePath is null)
                return;
            try
            {
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить временный файл {TempPath}", tempFilePath);
            }
        }

        // HEIC → JPEG: перекодируем оригинал до хеширования, чтобы дедуп, превью и веб-отдача
        // работали с JPEG (браузеры HEIC не отображают, ImageSharp его не декодирует).
        if (isHeic && tempFilePath is not null)
        {
            try
            {
                var jpegBytes = await _heicConverter.ConvertToJpegAsync(tempFilePath, cancellationToken);

                await originalStream.DisposeAsync();
                CleanupTempFile();
                tempFilePath = null;

                originalStream = new MemoryStream(jpegBytes);

                // С этого момента файл — JPEG: имя, content-type и размер обновляем соответственно.
                file.Filename = Path.ChangeExtension(file.Filename, ".jpg");
                contentType = "image/jpeg";
                fileSize = jpegBytes.Length;

                _logger.LogInformation("HEIC {FileId} сконвертирован в JPEG ({Size} байт)", file.Id, jpegBytes.Length);
            }
            catch (Exception ex)
            {
                // Фолбэк: если ffmpeg не справился — грузим оригинальный HEIC как есть (как было раньше).
                _logger.LogError(ex, "Не удалось сконвертировать HEIC {FileId} в JPEG, загружаю оригинал", file.Id);
                originalStream.Position = 0;
            }
        }

        // Compute SHA256 hash of the file
        string fileHash;
        using (var sha256 = SHA256.Create())
        {
            var hashBytes = await sha256.ComputeHashAsync(originalStream, cancellationToken);
            fileHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        originalStream.Position = 0;

        _logger.LogInformation("Вычислен хеш файла: {FileHash}", fileHash);

        // Серверная дедупликация: проверяем, существует ли файл с таким хешем
        var existingFileId = await _hashesStorage.GetFileIdByHash(fileHash);
        if (existingFileId.HasValue)
        {
            _logger.LogInformation(
                "Файл с хешем {FileHash} уже существует в хранилище (FileId: {ExistingFileId}). Дедупликация.",
                fileHash, existingFileId.Value);

            var existingFile = await _filesStorage.GetFile(existingFileId.Value);

            var canDeduplicate = existingFile is not null
                && !string.IsNullOrEmpty(existingFile.Etag)
                && existingFile.Type == file.Type;

            if (canDeduplicate)
            {
                foreach (var uploaderId in file.Uploaders)
                    await _filesStorage.AddUploaderToFile(existingFileId.Value, uploaderId);

                // Если у дедуплицированного оригинала есть превью — подцепляем владельцев и к ним,
                // чтобы корректно считать квоту по превью.
                var existingPreviews = await _filesStorage.GetPreviewsForFile(existingFileId.Value, cancellationToken);
                foreach (var prev in existingPreviews)
                {
                    foreach (var uploaderId in file.Uploaders)
                        await _filesStorage.AddUploaderToFile(prev.PreviewFileId, uploaderId);
                }

                await _filesStorage.DeleteFile(file.Id);
                await _hashesStorage.DeleteHashByFileId(file.Id, cancellationToken);
                await originalStream.DisposeAsync();
                CleanupTempFile();

                return existingFileId.Value.ToString();
            }

            _logger.LogInformation(
                "Дедупликация пропущена для {FileId}: тип отличается ({ExistingType} vs {NewType}) или файл не загружен.",
                file.Id, existingFile?.Type, file.Type);
        }

        // Превью генерируем только для облачных файлов с image/* контентом.
        // Аватары идут через UploadAvatarServer (там собственный пайплайн с 64px-thumb),
        // здесь FilePreview для них не создаём — намеренно.
        var isImageContent = contentType.StartsWith("image/");
        var needsDimensions = ImageTypesForDimensions.Contains(file.Type) && isImageContent;
        var needsCloudPreviews = file.Type == UploadFileType.CloudFile && isImageContent;

        List<MultiPreviewItem>? generatedPreviews = null;

        try
        {
            // 1) Размеры + сжатие оригинала (если нужно). Превью отдельным проходом ниже.
            if (needsDimensions)
            {
                originalStream.Position = 0;
                try
                {
                    var imageResult = await _imageCompressor.ProcessImageAllInOneAsync(
                        originalStream,
                        enforceOriginalLimits: false,
                        previewWidth: null,
                        cancellationToken);

                    file.ImageWidth = imageResult.Width;
                    file.ImageHeight = imageResult.Height;
                    _logger.LogInformation(
                        "Размеры изображения {FileId}: {Width}x{Height}",
                        file.Id, imageResult.Width, imageResult.Height);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось прочитать размеры изображения {FileId}", file.Id);
                    needsCloudPreviews = false;
                }
                finally
                {
                    originalStream.Position = 0;
                }
            }

            // 2) Мультиразмерные превью
            if (needsCloudPreviews)
            {
                originalStream.Position = 0;
                try
                {
                    generatedPreviews = await _imageCompressor.GenerateMultiplePreviewsAsync(
                        originalStream, CloudPreviewWidths, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось сгенерировать превью для {FileId}", file.Id);
                }
                finally
                {
                    originalStream.Position = 0;
                }
            }

            // 2b) Видео: размеры + кадр-обложка на 5-й секунде → тот же multi-preview pipeline.
            var needsVideoPreview = file.Type == UploadFileType.CloudFile && isVideoContent && tempFilePath is not null;
            if (needsVideoPreview)
            {
                try
                {
                    var (vw, vh, _) = await _videoThumbnailExtractor.ProbeAsync(tempFilePath!, cancellationToken);
                    if (vw > 0 && vh > 0)
                    {
                        file.ImageWidth = vw;
                        file.ImageHeight = vh;
                    }

                    var frameBytes = await _videoThumbnailExtractor.ExtractFrameJpegAsync(
                        tempFilePath!, VideoThumbnailExtractor.DefaultFramePosition, cancellationToken);

                    using var frameStream = new MemoryStream(frameBytes);
                    generatedPreviews = await _imageCompressor.GenerateMultiplePreviewsAsync(
                        frameStream, CloudPreviewWidths, cancellationToken);

                    _logger.LogInformation(
                        "Сгенерировано превью видео {FileId}: {Width}x{Height}, кадров={Count}",
                        file.Id, vw, vh, generatedPreviews?.Count ?? 0);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось сгенерировать превью видео {FileId}", file.Id);
                }
                finally
                {
                    originalStream.Position = 0;
                }
            }

            // 3) Грузим оригинал в S3
            var etag = await _s3Uploader.UploadAsync(
                bucketName,
                $"{file.Id}",
                originalStream,
                contentType
            );

            _logger.LogInformation("Файл успешно загружен в S3, получен Etag: {Etag}", etag);

            file.Etag = etag;
            file.UploadedAt = DateTime.UtcNow;
            file.Size = fileSize;
        }
        finally
        {
            await originalStream.DisposeAsync();
            CleanupTempFile();
        }

        // Сохраняем оригинал + его хеш
        await _filesStorage.UpdateFile(file);

        var fileHashEntity = new FileHash
        {
            FileId = file.Id,
            Hash = fileHash
        };
        await _hashesStorage.AddHash(fileHashEntity);

        _logger.LogInformation("Хеш файла сохранен в базу данных");

        // 4) Поднимаем превью в S3 + дедуп + FilePreview-связки
        if (generatedPreviews is { Count: > 0 })
        {
            await _previewPersistence.PersistPreviewsAsync(file, generatedPreviews, bucketName, cancellationToken);
        }

        _logger.LogInformation("Обработка файла {FileId} успешно завершена", file.Id);

        return file.Id.ToString();
    }
}
