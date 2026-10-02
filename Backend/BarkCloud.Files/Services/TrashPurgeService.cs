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
    /// Окончательно удаляет переданные записи корзины (ручное «Удалить навсегда» / «Очистить
    /// корзину»). Записи, которые к моменту удаления уже не в корзине, не трогаются.
    /// </summary>
    public Task<TrashPurgeResult> PurgeEntriesAsync(IReadOnlyCollection<CloudFileEntry> entries, CancellationToken cancellationToken)
        => PurgeCoreAsync(entries, expiredAt: null, cancellationToken);

    /// <summary>
    /// Окончательно удаляет переданные записи корзины, у которых срок хранения истёк к
    /// <paramref name="now"/> (фоновый воркер). Запись, восстановленная или повторно удалённая с
    /// более поздним PurgeAt после выборки, не трогается.
    /// </summary>
    public Task<TrashPurgeResult> PurgeExpiredEntriesAsync(IReadOnlyCollection<CloudFileEntry> entries, DateTime now, CancellationToken cancellationToken)
        => PurgeCoreAsync(entries, now, cancellationToken);

    /// <summary>
    /// Ядро зачистки. Точка невозврата — условное удаление строк записей в короткой транзакции БД:
    /// переданные <paramref name="entries"/> — лишь устаревший снимок, к моменту удаления запись
    /// могла быть восстановлена (или повторно удалена с новым PurgeAt). Дальше обрабатываются только
    /// реально удалённые строки; S3 трогается после коммита.
    /// </summary>
    private async Task<TrashPurgeResult> PurgeCoreAsync(
        IReadOnlyCollection<CloudFileEntry> entries, DateTime? expiredAt, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return default;

        var entryIds = entries.Select(e => e.Id).ToList();
        List<CloudFileEntry> purged;
        List<(long OwnerId, Guid FileId)> pairs;

        await using (var transaction = await _context.Database.BeginTransactionAsync(cancellationToken))
        {
            // 1. Gate: удаляем только то, что всё ещё в корзине (и, для воркера, всё ещё просрочено).
            //    Конкурирующее восстановление либо выполнилось раньше (условие не совпадёт), либо
            //    дождётся коммита и получит «записи нет».
            var gate = _context.CloudFileEntries.Where(e => entryIds.Contains(e.Id) && e.IsDeleted);
            if (expiredAt is { } cutoff)
                gate = gate.Where(e => e.PurgeAt != null && e.PurgeAt <= cutoff);
            await gate.ExecuteDeleteAsync(cancellationToken);

            // Реально удалены те записи, которых после gate больше нет в БД; оставшиеся
            // восстановлены/повторно удалены — их не трогаем.
            var survivors = (await _context.CloudFileEntries
                    .AsNoTracking()
                    .Where(e => entryIds.Contains(e.Id))
                    .Select(e => e.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            purged = entries.Where(e => !survivors.Contains(e.Id)).ToList();
            if (purged.Count == 0)
                return default;

            pairs = purged.Select(e => (e.OwnerId, e.FileId)).Distinct().ToList();

            // 2. Снимаем владельца с блоба и чистим привязки (альбомы, избранное, публичные ссылки,
            //    гранты доступа) только у пар, у которых не осталось ни одной записи (любого
            //    состояния). Если у файла есть другая запись (например, живая после повторной
            //    загрузки), её метаданные не трогаем. Сначала фиксируем освобождённые пары — превью
            //    обрабатываем отдельным проходом ниже.
            var released = new List<(long OwnerId, Guid FileId)>();
            foreach (var pair in pairs)
            {
                var stillReferenced = await _context.CloudFileEntries
                    .AnyAsync(e => e.OwnerId == pair.OwnerId && e.FileId == pair.FileId, cancellationToken);
                if (stillReferenced)
                    continue;

                await _context.AlbumItems
                    .Where(a => a.OwnerId == pair.OwnerId && a.FileId == pair.FileId)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.FavoriteFiles
                    .Where(f => f.OwnerId == pair.OwnerId && f.FileId == pair.FileId)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.FileSearchAliases
                    .Where(a => a.OwnerId == pair.OwnerId && a.FileId == pair.FileId)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.FileTags
                    .Where(t => t.OwnerId == pair.OwnerId && t.FileId == pair.FileId)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.ShareLinks
                    .Where(s => s.OwnerId == pair.OwnerId && s.FileId == pair.FileId)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.FileGrants
                    .Where(g => g.OwnerId == pair.OwnerId && g.FileId == pair.FileId)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.MusicPlaylistItems
                    .Where(i => i.OwnerId == pair.OwnerId && i.FileId == pair.FileId)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.RemoveUploaderAsync(pair.FileId, pair.OwnerId, cancellationToken);
                released.Add(pair);
            }

            // Снимаем владельца с превью-блобов. Превью дедуплицируются по SHA256, поэтому один
            // блоб-превью может быть привязан сразу к нескольким оригиналам. Убираем владельца с
            // превью ТОЛЬКО если у него не осталось другого (не удаляемого сейчас) оригинала,
            // ссылающегося на тот же превью-блоб, — иначе оставшийся файл лишился бы превью.
            // Строки превью блокируются до коммита (параллельная привязка нового оригинала или такой же
            // purge ждёт и видит результат), сразу все и по порядку Id — чтобы два purge с пересекающимися
            // превью разных владельцев не ждали друг друга.
            var previewsByOwner = new List<(long OwnerId, List<Guid> ReleasedFileIds, List<Guid> PreviewFileIds)>();
            foreach (var ownerGroup in released.GroupBy(r => r.OwnerId))
            {
                var releasedFileIds = ownerGroup.Select(r => r.FileId).ToList();
                var previewFileIds = await _context.FilePreviews
                    .AsNoTracking()
                    .Where(p => releasedFileIds.Contains(p.OriginalFileId))
                    .Select(p => p.PreviewFileId)
                    .Distinct()
                    .ToListAsync(cancellationToken);
                if (previewFileIds.Count > 0)
                    previewsByOwner.Add((ownerGroup.Key, releasedFileIds, previewFileIds));
            }

            await _context.LockLiveFilesAsync(
                previewsByOwner.SelectMany(x => x.PreviewFileIds).Distinct().ToList(), cancellationToken);
            foreach (var (ownerId, releasedFileIds, previewFileIds) in previewsByOwner)
                await _context.ReleasePreviewOwnerAsync(previewFileIds, ownerId, releasedFileIds, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        // 3. Вне транзакции физически удаляем осиротевшие блобы (оригиналы и их превью) из S3 и БД.
        var originalFileIds = pairs.Select(p => p.FileId).Distinct().ToList();
        var blobs = await PurgeOrphanBlobsAsync(originalFileIds, cancellationToken);

        _logger.LogInformation(
            "Окончательно удалено: записей {Entries}, осиротевших блобов из S3 {Orphans}",
            purged.Count, blobs);

        return new TrashPurgeResult(purged.Count, blobs);
    }

    /// <summary>
    /// Физически удаляет осиротевшие блобы (с пустым списком Uploaders) среди переданных
    /// кандидатов и связанных с ними превью: объект из S3, его хеш, связки FilePreview и строку
    /// UploadedFiles. Строка БД удаляется ТОЛЬКО при успешном удалении объекта из S3 — иначе блоб
    /// остаётся осиротевшим и будет повторно обработан фоновым <see cref="OrphanBlobCleanupService"/>.
    /// Блоб занимается условным удалением строки в транзакции ДО удаления из S3: если на него успели
    /// сослаться (владелец добавлен), он остаётся; если ссылаются позже, параллельная привязка превью
    /// ждёт на этой строке и после коммита видит, что блоба нет, — удалённый объект не «воскресает».
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
            .AsNoTracking()
            .Where(f => allIds.Contains(f.Id) && f.Uploaders.Count == 0)
            .ToListAsync(cancellationToken);

        // Удаляем по одному; при ошибке S3 блоб остаётся осиротевшим — фоновый воркер повторит
        // попытку позже, и объект не «протечёт» в S3.
        var deleted = 0;
        foreach (var orphan in orphans)
        {
            if (await TryDeleteOrphanAsync(orphan, cancellationToken))
                deleted++;
        }

        return deleted;
    }

    private async Task<bool> TryDeleteOrphanAsync(UploadFile orphan, CancellationToken cancellationToken)
    {
        // Без CommitAsync (блоб оказался не сирота или S3 не удалил объект) Dispose откатывает занятие строки.
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await _context.UploadedFiles
            .Where(f => f.Id == orphan.Id && f.Uploaders.Count == 0)
            .ExecuteDeleteAsync(cancellationToken);
        if (claimed == 0)
            return false;

        var storageProfileId = _bucketRegistry.ResolveReadProfileId(orphan);
        try
        {
            await _s3.DeleteAsync(storageProfileId, orphan.Id.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Не удалось удалить объект S3 (bucket={Bucket}, key={FileId}); блоб оставлен для повторной попытки",
                storageProfileId, orphan.Id);
            return false;
        }

        await _hashesStorage.DeleteHashByFileId(orphan.Id, cancellationToken);

        // Снимаем связки превью, ссылающиеся на удалённый блоб (как на оригинал, так и на превью).
        await _context.FilePreviews
            .Where(p => p.OriginalFileId == orphan.Id || p.PreviewFileId == orphan.Id)
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
