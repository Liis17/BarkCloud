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
public class TrashPurgeService
{
    /// <summary>Срок хранения файла в корзине до окончательного удаления.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private readonly FilesContext _context;
    private readonly S3Uploader _s3;
    private readonly S3BucketRegistry _bucketRegistry;
    private readonly FileHashesStorage _hashesStorage;
    private readonly ILogger<TrashPurgeService> _logger;

    public TrashPurgeService(
        FilesContext context,
        S3Uploader s3,
        S3BucketRegistry bucketRegistry,
        FileHashesStorage hashesStorage,
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

        // 1. Убираем файлы из альбомов и избранного владельца, удаляем сами записи иерархии.
        foreach (var pair in pairs)
        {
            await _context.AlbumItems
                .Where(a => a.OwnerId == pair.OwnerId && a.FileId == pair.FileId)
                .ExecuteDeleteAsync(cancellationToken);

            await _context.FavoriteFiles
                .Where(f => f.OwnerId == pair.OwnerId && f.FileId == pair.FileId)
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

        // 3. Физически удаляем осиротевшие блобы (Uploaders пуст): оригиналы и их превью.
        var originalFileIds = pairs.Select(p => p.FileId).Distinct().ToList();
        var previewLinks = await _context.FilePreviews
            .AsNoTracking()
            .Where(p => originalFileIds.Contains(p.OriginalFileId))
            .Select(p => p.PreviewFileId)
            .ToListAsync(cancellationToken);

        var candidateIds = originalFileIds.Concat(previewLinks).Distinct().ToList();
        var candidates = await _context.UploadedFiles
            .Where(f => candidateIds.Contains(f.Id))
            .ToListAsync(cancellationToken);

        var orphans = candidates.Where(f => f.Uploaders.Count == 0).ToList();
        if (orphans.Count == 0)
            return 0;

        var orphanIds = orphans.Select(f => f.Id).ToHashSet();

        // Удаляем связки превью, ссылающиеся на осиротевшие блобы (как оригиналы, так и превью).
        await _context.FilePreviews
            .Where(p => orphanIds.Contains(p.OriginalFileId) || orphanIds.Contains(p.PreviewFileId))
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var f in orphans)
        {
            var bucket = _bucketRegistry.GetBucketName(f.Type);
            try
            {
                await _s3.DeleteAsync(bucket, f.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить объект S3 (bucket={Bucket}, key={FileId})", bucket, f.Id);
            }

            await _hashesStorage.DeleteHashByFileId(f.Id, cancellationToken);
        }

        _context.UploadedFiles.RemoveRange(orphans);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Окончательно удалено: записей {Entries}, осиротевших блобов из S3 {Orphans}",
            entryIds.Count, orphans.Count);

        return orphans.Count;
    }
}
