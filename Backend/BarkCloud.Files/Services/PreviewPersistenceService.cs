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
        string storageProfileId,
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

            // Уже есть UploadFile с такими байтами — переиспользуем (владельцы + связка атомарно). Если блоб
            // к этому моменту удалён очисткой осиротевших, заливаем новый.
            var existingPreviewFileId = await _hashesStorage.GetFileIdByHash(previewHash, storageProfileId);
            if (existingPreviewFileId.HasValue
                && await TryLinkExistingBlobAsync(
                    original,
                    NewLink(original.Id, existingPreviewFileId.Value, item.TargetWidth, item.ActualWidth, item.ActualHeight),
                    cancellationToken))
                continue;

            var previewFileId = Guid.NewGuid();
            using var ms = new MemoryStream(item.Bytes);
            var previewEtag = await _s3Uploader.UploadAsync(storageProfileId, $"{previewFileId}", ms, "image/jpeg");

            var previewFile = new UploadFile
            {
                Id = previewFileId,
                Uploaders = original.Uploaders.ToList(),
                CreatedAt = DateTime.UtcNow,
                UploadedAt = DateTime.UtcNow,
                Etag = previewEtag,
                Type = UploadFileType.CloudFile,
                StorageProfileId = storageProfileId,
                MediaKind = MediaKind.Photo,
                Filename = $"preview_{item.TargetWidth}.jpg",
                Size = item.Bytes.Length,
                ImageWidth = item.ActualWidth,
                ImageHeight = item.ActualHeight
            };

            await _filesStorage.AddToStorage(previewFile);
            await _hashesStorage.AddHash(new FileHash { FileId = previewFileId, Hash = previewHash });

            _context.FilePreviews.Add(
                NewLink(original.Id, previewFileId, item.TargetWidth, item.ActualWidth, item.ActualHeight));
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
        string storageProfileId,
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

        var existingByHash = await _hashesStorage.GetFileIdByHash(viewHash, storageProfileId);
        if (existingByHash.HasValue
            && await TryLinkExistingBlobAsync(
                original, NewLink(original.Id, existingByHash.Value, 0, width, height), cancellationToken))
            return existingByHash.Value;

        var viewFileId = Guid.NewGuid();
        using var ms = new MemoryStream(jpegBytes);
        var etag = await _s3Uploader.UploadAsync(storageProfileId, $"{viewFileId}", ms, "image/jpeg");

        var viewFile = new UploadFile
        {
            Id = viewFileId,
            Uploaders = original.Uploaders.ToList(),
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
            Etag = etag,
            Type = UploadFileType.CloudFile,
            StorageProfileId = storageProfileId,
            MediaKind = MediaKind.Photo,
            Filename = "view.jpg",
            Size = jpegBytes.Length,
            ImageWidth = width > 0 ? width : null,
            ImageHeight = height > 0 ? height : null
        };

        await _filesStorage.AddToStorage(viewFile);
        await _hashesStorage.AddHash(new FileHash { FileId = viewFileId, Hash = viewHash });

        _context.FilePreviews.Add(NewLink(original.Id, viewFileId, 0, width, height));

        await _context.SaveChangesAsync(cancellationToken);
        return viewFileId;
    }

    private static FilePreview NewLink(Guid originalId, Guid previewFileId, int targetWidth, int actualWidth, int actualHeight) => new()
    {
        Id = Guid.NewGuid(),
        OriginalFileId = originalId,
        PreviewFileId = previewFileId,
        TargetWidth = targetWidth,
        ActualWidth = actualWidth,
        ActualHeight = actualHeight,
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>
    /// Привязывает оригинал к уже существующему превью-блобу: владельцы оригинала добавляются блобу и
    /// сохраняется связка — в одной транзакции под блокировкой строки блоба. Параллельное освобождение
    /// владельца или удаление осиротевшего блоба ждёт и не успевает вклиниться между проверкой и связкой.
    /// Возвращает false, если блоба уже нет или на него никто не ссылается (его удаляет очистка) —
    /// тогда ссылаться на него нельзя.
    /// </summary>
    private async Task<bool> TryLinkExistingBlobAsync(UploadFile original, FilePreview link, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        if ((await _context.LockLiveFilesAsync([link.PreviewFileId], cancellationToken)).Count == 0)
            return false;

        foreach (var uploaderId in original.Uploaders)
            await _filesStorage.AddUploaderToFile(link.PreviewFileId, uploaderId);

        _context.FilePreviews.Add(link);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
