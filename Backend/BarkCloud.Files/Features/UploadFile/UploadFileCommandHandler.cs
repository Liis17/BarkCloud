using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;

using MediatR;

using Microsoft.EntityFrameworkCore;

using System.Security.Cryptography;

using DomainUploadFile = BarkCloud.Files.Domain.UploadFile;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Features.UploadFile;

public class UploadFileCommandHandler : IRequestHandler<UploadFileCommand, string>
{
    private readonly UploadedFilesStorage _filesStorage;
    private readonly FileHashesStorage _hashesStorage;
    private readonly S3Uploader _s3Uploader;
    private readonly S3BucketRegistry _bucketRegistry;
    private readonly ImageCompressor _imageCompressor;
    private readonly FilesContext _context;
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
        UploadedFilesStorage filesStorage,
        FileHashesStorage hashesStorage,
        S3Uploader s3Uploader,
        S3BucketRegistry bucketRegistry,
        ImageCompressor imageCompressor,
        FilesContext context,
        ILogger<UploadFileCommandHandler> logger)
    {
        _filesStorage = filesStorage;
        _hashesStorage = hashesStorage;
        _s3Uploader = s3Uploader;
        _bucketRegistry = bucketRegistry;
        _imageCompressor = imageCompressor;
        _context = context;
        _logger = logger;
    }

    public async Task<string> Handle(UploadFileCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Начало обработки загрузки файла с ID: {FileId}", request.FileId);

        var file = await _filesStorage.GetFile(request.FileId);

        if (file is null)
        {
            _logger.LogError("Файл с ID {FileId} не найден", request.FileId);
            throw new Exception("File not found");
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

        // Получаем имя бакета в зависимости от типа файла
        var bucketName = _bucketRegistry.GetBucketName(file.Type);

        _logger.LogInformation("Загрузка файла {FileName} с типом {ContentType} в бакет {BucketName}",
            request.FileName, contentType, bucketName);

        long fileSize = request.FileSize > 0 ? request.FileSize : request.FileStream.Length;

        var isImageType = file.Type == UploadFileType.UserAvatar;

        Stream originalStream;

        if (!isImageType && fileSize > 100 * 1024 * 1024)
        {
            _logger.LogInformation("Файл {FileId} ({Size} МБ) буферизуется через диск", request.FileId, fileSize / 1024 / 1024);
            var tempStream = new FileStream(
                Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.DeleteOnClose);
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
            await PersistPreviewsAsync(file, generatedPreviews, bucketName, cancellationToken);
        }

        _logger.LogInformation("Обработка файла {FileId} успешно завершена", file.Id);

        return file.Id.ToString();
    }

    /// <summary>
    /// Сохраняет сгенерированные превью: дедуп по SHA256, загрузка в S3, привязка через FilePreview.
    /// </summary>
    private async Task PersistPreviewsAsync(
        DomainUploadFile original,
        List<MultiPreviewItem> previews,
        string bucketName,
        CancellationToken cancellationToken)
    {
        // Существующие превью — могли быть от другого оригинала с тем же контентом (теоретически),
        // или их нет вообще. Проверяем, чтобы не нарушить уникальный индекс (OriginalFileId, TargetWidth).
        var existingByWidth = (await _context.FilePreviews
                .Where(x => x.OriginalFileId == original.Id)
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.TargetWidth);

        foreach (var item in previews)
        {
            if (existingByWidth.ContainsKey(item.TargetWidth))
                continue;

            // SHA256 превью — для дедупликации
            string previewHash;
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(item.Bytes);
                previewHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            var existingPreviewFileId = await _hashesStorage.GetFileIdByHash(previewHash);
            Guid previewFileId;

            if (existingPreviewFileId.HasValue)
            {
                // Уже есть UploadFile с такими байтами — переиспользуем, добавляем владельцев.
                previewFileId = existingPreviewFileId.Value;
                foreach (var uploaderId in original.Uploaders)
                    await _filesStorage.AddUploaderToFile(previewFileId, uploaderId);
            }
            else
            {
                previewFileId = Guid.NewGuid();
                using var ms = new MemoryStream(item.Bytes);
                var previewEtag = await _s3Uploader.UploadAsync(bucketName, $"{previewFileId}", ms, "image/jpeg");

                var previewFile = new DomainUploadFile
                {
                    Id = previewFileId,
                    Uploaders = original.Uploaders.ToList(),
                    CreatedAt = DateTime.UtcNow,
                    UploadedAt = DateTime.UtcNow,
                    Etag = previewEtag,
                    Type = UploadFileType.CloudFile,
                    Filename = $"preview_{item.TargetWidth}.jpg",
                    Size = item.Bytes.Length,
                    ImageWidth = item.ActualWidth,
                    ImageHeight = item.ActualHeight
                };

                await _filesStorage.AddToStorage(previewFile);
                await _hashesStorage.AddHash(new FileHash { FileId = previewFileId, Hash = previewHash });
            }

            _context.FilePreviews.Add(new FilePreview
            {
                Id = Guid.NewGuid(),
                OriginalFileId = original.Id,
                PreviewFileId = previewFileId,
                TargetWidth = item.TargetWidth,
                ActualWidth = item.ActualWidth,
                ActualHeight = item.ActualHeight,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
