using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

/// <summary>
/// Окончательная зачистка записей корзины: снятие владения, удаление из альбомов, удаление
/// строк БД и физическое удаление осиротевших блобов (и их превью) из S3. Используется и
/// фоновым воркером (<see cref="TrashCleanupService"/>), и ручными RPC «Удалить навсегда» /
/// «Очистить корзину».
/// </summary>
public class TrashPurgeService : ITrashPurgeService
{
    /// <summary>Срок хранения файла в корзине до окончательного удаления.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private readonly FilesContext _context;
    private readonly S3Uploader _s3;
    private readonly S3BucketRegistry _bucketRegistry;
    private readonly IFileHashesStorage _hashesStorage;
    private readonly ILogger<TrashPurgeService> _logger;

    public TrashPurgeService(
        FilesContext context,
        S3Uploader s3,
        S3BucketRegistry bucketRegistry,
        IFileHashesStorage hashesStorage,
        ILogger<TrashPurgeService> logger)
    {
        _context = context;
        _s3 = s3;
        _bucketRegistry = bucketRegistry;
        _hashesStorage = hashesStorage;
        _logger = logger;
    }

    /// <summary>
    /// Окончательно удаляет переданные записи корзины. Возвращает число физически удалённых
    /// из S3 блобов (оригиналов + превью).
    /// </summary>
    public async Task<int> PurgeEntriesAsync(IReadOnlyCollection<CloudFileEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return 0;

        var pairs = entries
            .Select(e => new { e.OwnerId, e.FileId })
            .Distinct()
            .ToList();
        var entryIds = entries.Select(e => e.Id).ToList();

        // 1. Убираем файлы из альбомов, избранного, публичных ссылок и грантов доступа владельца,
        //    удаляем сами записи иерархии.
        foreach (var pair in pairs)
        {
            await _context.AlbumItems
                .Where(a => a.OwnerId == pair.OwnerId && a.FileId == pair.FileId)
                .ExecuteDeleteAsync(cancellationToken);

            await _context.FavoriteFiles
                .Where(f => f.OwnerId == pair.OwnerId && f.FileId == pair.FileId)
                .ExecuteDeleteAsync(cancellationToken);

            await _context.ShareLinks
                .Where(s => s.OwnerId == pair.OwnerId && s.FileId == pair.FileId)
                .ExecuteDeleteAsync(cancellationToken);

            await _context.FileGrants
                .Where(g => g.OwnerId == pair.OwnerId && g.FileId == pair.FileId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await _context.CloudFileEntries
            .Where(e => entryIds.Contains(e.Id))
            .ExecuteDeleteAsync(cancellationToken);

        // 2. Снимаем владельца с блоба и его превью, если у него не осталось ни одной записи
        //    (любого состояния) на этот файл — повторяет логику декремента из ручного удаления.
        foreach (var pair in pairs)
        {
            var stillReferenced = await _context.CloudFileEntries
                .AnyAsync(e => e.OwnerId == pair.OwnerId && e.FileId == pair.FileId, cancellationToken);
            if (stillReferenced)
                continue;

            var uploadFile = await _context.UploadedFiles
                .FirstOrDefaultAsync(f => f.Id == pair.FileId, cancellationToken);
            uploadFile?.Uploaders.Remove(pair.OwnerId);

            var previewFileIds = await _context.FilePreviews
                .AsNoTracking()
                .Where(p => p.OriginalFileId == pair.FileId)
                .Select(p => p.PreviewFileId)
                .ToListAsync(cancellationToken);

            if (previewFileIds.Count > 0)
            {
                var previewFiles = await _context.UploadedFiles
                    .Where(f => previewFileIds.Contains(f.Id))
                    .ToListAsync(cancellationToken);

                foreach (var pf in previewFiles)
                    pf.Uploaders.Remove(pair.OwnerId);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        // 3. Физически удаляем осиротевшие блобы (оригиналы и их превью) из S3 и БД.
        var originalFileIds = pairs.Select(p => p.FileId).Distinct().ToList();
        var purged = await PurgeOrphanBlobsAsync(originalFileIds, cancellationToken);

        _logger.LogInformation(
            "Окончательно удалено: записей {Entries}, осиротевших блобов из S3 {Orphans}",
            entryIds.Count, purged);

        return purged;
    }

    /// <summary>
    /// Физически удаляет осиротевшие блобы (с пустым списком Uploaders) среди переданных
    /// кандидатов и связанных с ними превью: объект из S3, его хеш, связки FilePreview и строку
    /// UploadedFiles. Строка БД удаляется ТОЛЬКО при успешном удалении объекта из S3 — иначе блоб
    /// остаётся осиротевшим и будет повторно обработан фоновым <see cref="OrphanBlobCleanupService"/>.
    /// Возвращает число физически удалённых блобов.
    /// </summary>
    public async Task<int> PurgeOrphanBlobsAsync(IReadOnlyCollection<Guid> candidateFileIds, CancellationToken cancellationToken)
    {
        if (candidateFileIds.Count == 0)
            return 0;

        // Расширяем кандидатов их превью-блобами — они осиротевают вместе с оригиналом.
        var previewFileIds = await _context.FilePreviews
            .AsNoTracking()
            .Where(p => candidateFileIds.Contains(p.OriginalFileId))
            .Select(p => p.PreviewFileId)
            .ToListAsync(cancellationToken);

        var allIds = candidateFileIds.Concat(previewFileIds).Distinct().ToList();

        var orphans = await _context.UploadedFiles
            .Where(f => allIds.Contains(f.Id) && f.Uploaders.Count == 0)
            .ToListAsync(cancellationToken);
        if (orphans.Count == 0)
            return 0;

        // Удаляем из S3 по одному; строку БД сносим только при успехе. При ошибке блоб остаётся
        // осиротевшим — фоновый воркер повторит попытку позже, и объект не «протечёт» в S3.
        var deleted = new List<UploadFile>();
        foreach (var f in orphans)
        {
            var bucket = _bucketRegistry.GetBucketName(f.Type);
            try
            {
                await _s3.DeleteAsync(bucket, f.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Не удалось удалить объект S3 (bucket={Bucket}, key={FileId}); блоб оставлен для повторной попытки",
                    bucket, f.Id);
                continue;
            }

            await _hashesStorage.DeleteHashByFileId(f.Id, cancellationToken);
            deleted.Add(f);
        }

        if (deleted.Count == 0)
            return 0;

        var deletedIds = deleted.Select(f => f.Id).ToHashSet();

        // Снимаем связки превью, ссылающиеся на удалённые блобы (как на оригиналы, так и на превью).
        await _context.FilePreviews
            .Where(p => deletedIds.Contains(p.OriginalFileId) || deletedIds.Contains(p.PreviewFileId))
            .ExecuteDeleteAsync(cancellationToken);

        _context.UploadedFiles.RemoveRange(deleted);
        await _context.SaveChangesAsync(cancellationToken);

        return deleted.Count;
    }
}
