using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using MediatR;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Features.DownloadFile;

public class DownloadFileCommandHandler : IRequestHandler<DownloadFileCommand, DownloadFileResult>
{
    private readonly UploadedFilesStorage _filesStorage;
    private readonly S3Uploader _s3Uploader;
    private readonly S3BucketRegistry _bucketRegistry;
    private readonly TempFilesStorage _tempFilesStorage;
    private readonly FilesContext _context;
    private readonly ILogger<DownloadFileCommandHandler> _logger;

    public DownloadFileCommandHandler(UploadedFilesStorage filesStorage, S3Uploader s3Uploader,
        S3BucketRegistry bucketRegistry, TempFilesStorage tempFilesStorage,
        FilesContext context,
        ILogger<DownloadFileCommandHandler> logger)
    {
        _filesStorage = filesStorage;
        _s3Uploader = s3Uploader;
        _bucketRegistry = bucketRegistry;
        _tempFilesStorage = tempFilesStorage;
        _context = context;
        _logger = logger;
    }

    public async Task<DownloadFileResult> Handle(DownloadFileCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Запрос на скачивание файла {FileId}", request.FileId);

        var file = await _filesStorage.GetFile(request.FileId);

        // Проверяем, является ли этот id превью-файлом (FilePreview.PreviewFileId).
        // Превью раздаются публично, потому что URL на них возвращается в GetFileData
        // и предназначен для прямой отдачи в UI без TempFile-ссылок.
        var isPreviewFile = file is not null && await _context.FilePreviews
            .AsNoTracking()
            .AnyAsync(p => p.PreviewFileId == file.Id, cancellationToken);

        // По оригинальному ID можно качать только аватарки и превью-файлы.
        // Остальное (полные cloud-файлы) — только через временные ссылки.
        if (file is not null && file.Type != UploadFileType.UserAvatar && !isPreviewFile)
        {
            _logger.LogWarning(
                "Попытка доступа к файлу {FileId} с недопустимым типом {FileType}",
                request.FileId,
                file.Type
            );
            throw new Exception("Файл не найден");
        }

        // Это временная ссылка
        if (file is null)
        {
            _logger.LogDebug("Поиск временной ссылки для {FileId}", request.FileId);
            var tempFile = await _tempFilesStorage.GetTempFile(request.FileId);

            if (tempFile != null)
            {
                _logger.LogDebug(
                    "Найдена временная ссылка {TempFileId} -> оригинальный файл {OriginalFileId}",
                    request.FileId,
                    tempFile.OriginalFileId
                );
                file = await _filesStorage.GetFile(tempFile.OriginalFileId);
            }
        }

        if (file is null)
        {
            _logger.LogWarning("Файл {FileId} не найден", request.FileId);
            throw new Exception("Файл не найден");
        }

        if (string.IsNullOrEmpty(file.Etag))
        {
            _logger.LogWarning("Файл {FileId} ещё не был загружен", file.Id);
            throw new FileNotUploadedException("Файл ещё не был загружен");
        }

        var contentType = file.Filename.GetContentType();
        var extension = Path.GetExtension(file.Filename).ToLowerInvariant();
        var bucketName = _bucketRegistry.GetBucketName(file.Type);

        _logger.LogDebug(
            "Скачивание файла {FileId} из S3. Bucket: {BucketName}, Размер: {FileSize} байт",
            file.Id,
            bucketName,
            file.Size
        );

        var fileStream = await _s3Uploader.DownloadAsync(
            bucketName,
            $"{file.Id}"
        );

        _logger.LogInformation(
            "Файл {FileId} ({FileName}) успешно скачан. Тип: {FileType}, Размер: {FileSize} байт",
            file.Id,
            file.Filename,
            file.Type,
            file.Size
        );

        return new DownloadFileResult
        {
            FileStream = fileStream,
            FileName = $"{file.Id}{extension}",
            ContentType = contentType
        };
    }
}
