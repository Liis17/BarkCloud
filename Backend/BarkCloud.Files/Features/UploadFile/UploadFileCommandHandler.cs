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
    private readonly IFileMetadataStorage _metadataStorage;
    private readonly S3Uploader _s3Uploader;
    private readonly S3BucketRegistry _bucketRegistry;
    private readonly ImageCompressor _imageCompressor;
    private readonly VideoThumbnailExtractor _videoThumbnailExtractor;
    private readonly AudioMetadataExtractor? _audioMetadataExtractor;
    private readonly HeicImageConverter _heicConverter;
    private readonly FileMetadataExtractor _metadataExtractor;
    private readonly PreviewPersistenceService _previewPersistence;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<UploadFileCommandHandler> _logger;

    /// <summary>
    /// Ширины превью для облачных изображений (по убыванию — самое крупное первым).
    /// </summary>
    private static readonly int[] CloudPreviewWidths = { 1024, 512, 128 };

    private static readonly int[] AudioCoverWidths = { 512, 128 };

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
        IFileMetadataStorage metadataStorage,
        S3Uploader s3Uploader,
        S3BucketRegistry bucketRegistry,
        ImageCompressor imageCompressor,
        VideoThumbnailExtractor videoThumbnailExtractor,
        HeicImageConverter heicConverter,
        FileMetadataExtractor metadataExtractor,
        PreviewPersistenceService previewPersistence,
        ILogger<UploadFileCommandHandler> logger,
        FileActivityWriter? activity = null,
        AudioMetadataExtractor? audioMetadataExtractor = null)
    {
        _filesStorage = filesStorage;
        _hashesStorage = hashesStorage;
        _metadataStorage = metadataStorage;
        _s3Uploader = s3Uploader;
        _bucketRegistry = bucketRegistry;
        _imageCompressor = imageCompressor;
        _videoThumbnailExtractor = videoThumbnailExtractor;
        _audioMetadataExtractor = audioMetadataExtractor;
        _heicConverter = heicConverter;
        _metadataExtractor = metadataExtractor;
        _previewPersistence = previewPersistence;
        _activity = activity ?? FileActivityWriter.Noop;
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
        var isAudioContent = contentType.StartsWith("audio/");
        // HEIC/HEIF ImageSharp не декодирует — перекодируем в JPEG через ffmpeg (по файлу на диске).
        var isHeic = contentType == "image/heic";

        Stream originalStream;
        // Путь к временному файлу на диске. Нужен для видео и HEIC (FFmpeg читает файл по пути),
        // а также используется для буферизации больших не-картинок. null = буфер в памяти.
        string? tempFilePath = null;

        // Видео и HEIC всегда кладём на диск (FFmpeg работает с файлом), как и большие не-картинки.
        if (isVideoContent || isAudioContent || isHeic || (!isImageType && fileSize > 100 * 1024 * 1024))
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

        // Метаданные (EXIF/QuickTime/PDF/Office) — извлекаются по ходу пайплайна
        // в подходящие моменты (HEIC — до конвертации, JPEG/PNG — перед превью, видео —
        // вместе с ffprobe, документы — отдельным блоком), и сохраняются в конце.
        FileMetadata? extractedMetadata = null;

        // HEIC EXIF: извлекаем оригинальные теги ДО конвертации в JPEG —
        // ffmpeg `-frames:v 1` теряет EXIF, поэтому после конвертации брать неоткуда.
        if (isHeic && tempFilePath is not null)
        {
            try
            {
                originalStream.Position = 0;
                extractedMetadata = _metadataExtractor.ExtractFromImage(originalStream);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось извлечь EXIF из HEIC {FileId}", file.Id);
            }
            finally
            {
                originalStream.Position = 0;
            }
        }

        // HEIC оригинал НЕ конвертируем — храним как есть, чтобы его SHA256 совпадал
        // с тем, что считает клиент (иначе ломается дедуп и индикатор «уже в облаке»).
        // Но ImageSharp HEIC не декодирует, поэтому для размеров/превью и для JpegView
        // готовим отдельное полноразмерное JPEG-представление через ffmpeg.
        byte[]? heicJpegBytes = null;
        if (isHeic && tempFilePath is not null)
        {
            try
            {
                heicJpegBytes = await _heicConverter.ConvertToJpegAsync(tempFilePath, cancellationToken);
                _logger.LogInformation(
                    "HEIC {FileId}: получено JPEG-представление ({Size} байт), оригинал сохраняется как есть",
                    file.Id, heicJpegBytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось получить JPEG-представление HEIC {FileId}", file.Id);
            }
            finally
            {
                originalStream.Position = 0;
            }
        }

        // Стрим, который умеет декодировать ImageSharp: HEIC → его JPEG-представление,
        // прочие изображения → сам оригинал. null — нечем декодировать (HEIC без конверсии).
        MemoryStream? heicViewStream = heicJpegBytes is not null ? new MemoryStream(heicJpegBytes) : null;

        // Compute SHA256 hash of the file
        string fileHash;
        using (var sha256 = SHA256.Create())
        {
            var hashBytes = await sha256.ComputeHashAsync(originalStream, cancellationToken);
            fileHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        originalStream.Position = 0;

        _logger.LogInformation("Вычислен хеш файла: {FileHash}", fileHash);

        // Дедупликация по хешу намеренно отключена: одинаковый контент сохраняется как
        // отдельные независимые блобы (каждая загрузка — свой file.Id и своя строка FileHash;
        // индекс по Hash неуникальный). Хеш по-прежнему пишется в БД для проверок наличия
        // (CheckFileHash/CheckFileHashes) — например, чтобы автозагрузка iOS пропускала уже залитое.

        // Превью генерируем только для облачных файлов с image/* контентом.
        // Аватары идут через UploadAvatarServer (там собственный пайплайн с 64px-thumb),
        // здесь FilePreview для них не создаём — намеренно.
        var isImageContent = contentType.StartsWith("image/");
        var needsDimensions = ImageTypesForDimensions.Contains(file.Type) && isImageContent;
        var needsCloudPreviews = file.Type == UploadFileType.CloudFile && isImageContent;

        List<MultiPreviewItem>? generatedPreviews = null;

        // Источник для ImageSharp (размеры/превью/JpegView): HEIC → его JPEG-представление,
        // прочие изображения → сам оригинал. null — нечем декодировать (HEIC без конверсии).
        Stream? imageViewStream = isHeic ? heicViewStream : (isImageContent ? originalStream : null);
        if (isImageContent && imageViewStream is null)
        {
            needsDimensions = false;
            needsCloudPreviews = false;
        }
        // Полноразмерный JPEG для просмотра в вебе/не-Apple. Заполняется ниже.
        byte[]? jpegViewBytes = null;

        try
        {
            // 1) Размеры + сжатие оригинала (если нужно). Превью отдельным проходом ниже.
            if (needsDimensions && imageViewStream is not null)
            {
                imageViewStream.Position = 0;
                try
                {
                    var imageResult = await _imageCompressor.ProcessImageAllInOneAsync(
                        imageViewStream,
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
                    imageViewStream.Position = 0;
                }
            }

            // 2) Мультиразмерные превью
            if (needsCloudPreviews && imageViewStream is not null)
            {
                imageViewStream.Position = 0;
                try
                {
                    generatedPreviews = await _imageCompressor.GenerateMultiplePreviewsAsync(
                        imageViewStream, CloudPreviewWidths, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось сгенерировать превью для {FileId}", file.Id);
                }
                finally
                {
                    imageViewStream.Position = 0;
                }
            }

            // 2-jpeg) Полноразмерный JPEG-вид (JPEG 90%) для всех изображений-облаков:
            // HEIC уже сконвертирован (heicJpegBytes); прочие — перекодируем сами, включая
            // JPEG-оригинал (отдаём перекодированную копию, а не сам файл).
            if (file.Type == UploadFileType.CloudFile && isImageContent)
            {
                if (isHeic)
                {
                    jpegViewBytes = heicJpegBytes;
                }
                else if (imageViewStream is not null)
                {
                    try
                    {
                        imageViewStream.Position = 0;
                        jpegViewBytes = await _imageCompressor.EncodeFullJpegAsync(imageViewStream, 90, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось сгенерировать JpegView для {FileId}", file.Id);
                    }
                    finally
                    {
                        imageViewStream.Position = 0;
                    }
                }
            }

            // 2a) EXIF для НЕ-HEIC изображений — после probing размеров, до S3-аплоада.
            // HEIC обработан выше отдельно (до конвертации), здесь не дублируем.
            if (isImageContent && !isHeic && extractedMetadata is null)
            {
                try
                {
                    originalStream.Position = 0;
                    extractedMetadata = _metadataExtractor.ExtractFromImage(originalStream);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось извлечь EXIF из изображения {FileId}", file.Id);
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
                    var probe = await _videoThumbnailExtractor.ProbeFullAsync(tempFilePath!, cancellationToken);
                    var (vw, vh) = (probe.Width, probe.Height);
                    if (vw > 0 && vh > 0)
                    {
                        file.ImageWidth = vw;
                        file.ImageHeight = vh;
                    }

                    extractedMetadata ??= _metadataExtractor.ExtractFromVideo(probe);

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

            // 2b-audio) Аудио: длительность/теги + embedded artwork → квадратные обложки 512/128.
            var needsAudioMetadata = file.Type == UploadFileType.CloudFile && isAudioContent && tempFilePath is not null && _audioMetadataExtractor is not null;
            if (needsAudioMetadata)
            {
                try
                {
                    var audioExtractor = _audioMetadataExtractor!;
                    var probe = await audioExtractor.ProbeAsync(tempFilePath!, cancellationToken);
                    extractedMetadata ??= audioExtractor.ExtractMetadata(probe);

                    var artworkBytes = await audioExtractor.ExtractArtworkJpegAsync(tempFilePath!, cancellationToken);
                    if (artworkBytes is { Length: > 0 })
                    {
                        using var artworkStream = new MemoryStream(artworkBytes);
                        generatedPreviews = await _imageCompressor.GenerateSquarePreviewsAsync(
                            artworkStream, AudioCoverWidths, cancellationToken);
                    }

                    _logger.LogInformation(
                        "Обработано аудио {FileId}: duration={Duration}, artwork={HasArtwork}",
                        file.Id, probe.Duration, generatedPreviews is { Count: > 0 });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось извлечь метаданные аудио {FileId}", file.Id);
                }
                finally
                {
                    originalStream.Position = 0;
                }
            }

            // 2c) PDF / Office — отдельные парсеры; извлекаем перед загрузкой в S3.
            if (extractedMetadata is null && file.Type == UploadFileType.CloudFile)
            {
                try
                {
                    originalStream.Position = 0;
                    if (contentType == "application/pdf")
                    {
                        extractedMetadata = _metadataExtractor.ExtractFromPdf(originalStream);
                    }
                    else if (contentType.StartsWith("application/vnd.openxmlformats-officedocument."))
                    {
                        extractedMetadata = _metadataExtractor.ExtractFromOffice(originalStream, contentType);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось извлечь метаданные документа {FileId}", file.Id);
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

        // JpegView: для всех изображений-облаков сохраняем отдельный полноразмерный JPEG-блоб.
        // Он регистрируется как превью (TargetWidth=0), поэтому раздаётся публично, автоматически
        // исключается из галереи и чистится при удалении оригинала. Сам оригинал остаётся
        // доступен только по временным ссылкам.
        if (file.Type == UploadFileType.CloudFile && isImageContent && jpegViewBytes is not null)
        {
            try
            {
                file.JpegViewFileId = await _previewPersistence.PersistJpegViewAsync(
                    file, jpegViewBytes, file.ImageWidth ?? 0, file.ImageHeight ?? 0, bucketName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить JpegView-блоб для {FileId}", file.Id);
            }
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

        // 5) Метаданные блоба. Сохраняем только для CloudFile (для аватаров не имеет смысла).
        if (extractedMetadata is not null && file.Type == UploadFileType.CloudFile)
        {
            extractedMetadata.FileId = file.Id;
            extractedMetadata.CreatedAt = DateTime.UtcNow;
            try
            {
                await _metadataStorage.AddIfMissing(extractedMetadata, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить FileMetadata для {FileId}", file.Id);
            }
        }

        _logger.LogInformation("Обработка файла {FileId} успешно завершена", file.Id);

        var ownerId = file.Uploaders.FirstOrDefault();
        if (ownerId > 0 && file.Type == UploadFileType.CloudFile)
        {
            await _activity.AddAsync(
                ownerId,
                file.Id,
                ownerId,
                FileActivityKind.Uploaded,
                "Файл загружен",
                details: new { fileName = file.Filename, size = file.Size, mediaKind = file.MediaKind.ToString() },
                cancellationToken: cancellationToken);
        }

        return file.Id.ToString();
    }
}
