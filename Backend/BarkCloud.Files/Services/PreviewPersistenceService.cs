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
    private readonly UploadedFilesStorage _filesStorage;
    private readonly FileHashesStorage _hashesStorage;
    private readonly S3Uploader _s3Uploader;
    private readonly FilesContext _context;
    private readonly ILogger<PreviewPersistenceService> _logger;

    public PreviewPersistenceService(
        UploadedFilesStorage filesStorage,
        FileHashesStorage hashesStorage,
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
    public async Task PersistPreviewsAsync(
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
}
