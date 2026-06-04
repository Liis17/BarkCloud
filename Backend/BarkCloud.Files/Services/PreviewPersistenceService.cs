using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

using System.Security.Cryptography;

namespace BarkCloud.Files.Services;

/// <summary>
/// Сохранение сгенерированных превью: дедупликация по SHA256, заливка в S3 и создание
/// связок <see cref="FilePreview"/>. Используется при загрузке (фото/видео) и при ручной
/// смене превью видео, чтобы логика квот/дедупа не расходилась между местами вызова.
/// </summary>
public class PreviewPersistenceService
{
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly IFileHashesStorage _hashesStorage;
    private readonly S3Uploader _s3Uploader;
    private readonly FilesContext _context;
    private readonly ILogger<PreviewPersistenceService> _logger;

    public PreviewPersistenceService(
        IUploadedFilesStorage filesStorage,
        IFileHashesStorage hashesStorage,
        S3Uploader s3Uploader,
        FilesContext context,
        ILogger<PreviewPersistenceService> logger)
    {
        _filesStorage = filesStorage;
        _hashesStorage = hashesStorage;
        _s3Uploader = s3Uploader;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Сохраняет превью для оригинала <paramref name="original"/>: дедуп по SHA256, заливка в S3,
    /// привязка через FilePreview. Уже существующие превью тех же ширин пропускаются.
    /// </summary>
    public virtual async Task PersistPreviewsAsync(
        UploadFile original,
        List<MultiPreviewItem> previews,
        string bucketName,
        CancellationToken cancellationToken)
    {
        // Существующие превью — чтобы не нарушить уникальный индекс (OriginalFileId, TargetWidth).
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

                var previewFile = new UploadFile
                {
                    Id = previewFileId,
                    Uploaders = original.Uploaders.ToList(),
                    CreatedAt = DateTime.UtcNow,
                    UploadedAt = DateTime.UtcNow,
                    Etag = previewEtag,
                    Type = UploadFileType.CloudFile,
                    MediaKind = MediaKind.Photo,
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

    /// <summary>
    /// Сохраняет полноразмерный JPEG-вид оригинала («JpegView») как отдельный блоб и
    /// связывает его через <see cref="FilePreview"/> со служебной шириной <c>TargetWidth = 0</c>.
    /// За счёт этой связки блоб автоматически исключается из галереи (листинги пропускают
    /// превью-блобы) и чистится при удалении оригинала. Возвращает file_id вида.
    /// Дедуп по SHA256 — как у обычных превью.
    /// </summary>
    public virtual async Task<Guid> PersistJpegViewAsync(
        UploadFile original,
        byte[] jpegBytes,
        int width,
        int height,
        string bucketName,
        CancellationToken cancellationToken)
    {
        var existing = await _context.FilePreviews
            .FirstOrDefaultAsync(x => x.OriginalFileId == original.Id && x.TargetWidth == 0, cancellationToken);
        if (existing is not null)
            return existing.PreviewFileId;

        string viewHash;
        using (var sha256 = SHA256.Create())
        {
            viewHash = Convert.ToHexString(sha256.ComputeHash(jpegBytes)).ToLowerInvariant();
        }

        var existingByHash = await _hashesStorage.GetFileIdByHash(viewHash);
        Guid viewFileId;

        if (existingByHash.HasValue)
        {
            viewFileId = existingByHash.Value;
            foreach (var uploaderId in original.Uploaders)
                await _filesStorage.AddUploaderToFile(viewFileId, uploaderId);
        }
        else
        {
            viewFileId = Guid.NewGuid();
            using var ms = new MemoryStream(jpegBytes);
            var etag = await _s3Uploader.UploadAsync(bucketName, $"{viewFileId}", ms, "image/jpeg");

            var viewFile = new UploadFile
            {
                Id = viewFileId,
                Uploaders = original.Uploaders.ToList(),
                CreatedAt = DateTime.UtcNow,
                UploadedAt = DateTime.UtcNow,
                Etag = etag,
                Type = UploadFileType.CloudFile,
                MediaKind = MediaKind.Photo,
                Filename = "view.jpg",
                Size = jpegBytes.Length,
                ImageWidth = width > 0 ? width : null,
                ImageHeight = height > 0 ? height : null
            };

            await _filesStorage.AddToStorage(viewFile);
            await _hashesStorage.AddHash(new FileHash { FileId = viewFileId, Hash = viewHash });
        }

        _context.FilePreviews.Add(new FilePreview
        {
            Id = Guid.NewGuid(),
            OriginalFileId = original.Id,
            PreviewFileId = viewFileId,
            TargetWidth = 0,
            ActualWidth = width,
            ActualHeight = height,
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync(cancellationToken);
        return viewFileId;
    }
}
