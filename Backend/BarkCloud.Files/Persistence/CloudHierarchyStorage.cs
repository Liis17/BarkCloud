using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище иерархии облачных папок и файловых записей пользователя.
/// Корневая папка не материализуется — её представляет ParentId == null / DirectoryId == null
/// в запросах, но для CloudFileEntry мы используем выделенный синтетический Guid.Empty
/// в качестве идентификатора корневой папки на уровне хранения.
/// </summary>
public class CloudHierarchyStorage : ICloudHierarchyStorage
{
    /// <summary>
    /// Синтетический идентификатор корневой директории владельца.
    /// Используется только для CloudFileEntry.DirectoryId, чтобы поддерживать
    /// уникальный индекс (OwnerId, DirectoryId, Name). Сама запись CloudDirectory
    /// с таким Id никогда не создаётся.
    /// </summary>
    public static readonly Guid RootDirectoryId = Guid.Empty;

    private readonly FilesContext _context;

    public CloudHierarchyStorage(FilesContext context)
    {
        _context = context;
    }

    // ===== Directories =====

    public async Task<CloudDirectory?> GetDirectory(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CloudDirectories
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<CloudDirectory?> GetDirectoryAsNoTracking(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CloudDirectories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<bool> DirectoryNameExists(long ownerId, Guid? parentId, string name, CancellationToken cancellationToken = default)
    {
        return await _context.CloudDirectories
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.ParentId == parentId && x.Name == name, cancellationToken);
    }

    public async Task<CloudDirectory> AddDirectory(CloudDirectory directory, CancellationToken cancellationToken = default)
    {
        _context.CloudDirectories.Add(directory);
        await _context.SaveChangesAsync(cancellationToken);
        return directory;
    }

    public async Task UpdateDirectory(CloudDirectory directory, CancellationToken cancellationToken = default)
    {
        _context.CloudDirectories.Update(directory);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<CloudDirectory>> ListSubdirectories(long ownerId, Guid? parentId, CancellationToken cancellationToken = default)
    {
        return await _context.CloudDirectories
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.ParentId == parentId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Возвращает все папки в поддереве, включая саму root-папку.
    /// Реализовано итеративным обходом, без рекурсивных SQL CTE — это работает
    /// одинаково на любом провайдере и не требует raw-SQL.
    /// </summary>
    public async Task<List<CloudDirectory>> GetSubtree(long ownerId, Guid rootDirectoryId, CancellationToken cancellationToken = default)
    {
        var result = new List<CloudDirectory>();
        var frontier = new Queue<Guid>();

        var root = await _context.CloudDirectories
            .FirstOrDefaultAsync(x => x.Id == rootDirectoryId && x.OwnerId == ownerId, cancellationToken);
        if (root is null)
            return result;

        result.Add(root);
        frontier.Enqueue(root.Id);

        while (frontier.Count > 0)
        {
            var batch = new List<Guid>();
            while (frontier.Count > 0 && batch.Count < 256)
                batch.Add(frontier.Dequeue());

            var children = await _context.CloudDirectories
                .Where(x => x.OwnerId == ownerId && x.ParentId != null && batch.Contains(x.ParentId!.Value))
                .ToListAsync(cancellationToken);

            foreach (var child in children)
            {
                result.Add(child);
                frontier.Enqueue(child.Id);
            }
        }

        return result;
    }

    public void RemoveDirectories(IEnumerable<CloudDirectory> directories)
    {
        _context.CloudDirectories.RemoveRange(directories);
    }

    // ===== File entries =====

    public async Task<CloudFileEntry?> GetFileEntry(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>
    /// Живые (не в корзине) записи владельца по набору id записей — отслеживаемые,
    /// для массового мягкого удаления (мутация IsDeleted/DeletedAt/PurgeAt). Чужие,
    /// несуществующие и уже удалённые id просто не попадают в выборку.
    /// </summary>
    public async Task<List<CloudFileEntry>> GetLiveFileEntriesByIds(long ownerId, IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken = default)
    {
        if (entryIds.Count == 0)
            return new List<CloudFileEntry>();

        return await _context.CloudFileEntries
            .Where(e => e.OwnerId == ownerId && entryIds.Contains(e.Id) && !e.IsDeleted)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> FileEntryNameExists(long ownerId, Guid directoryId, string name, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.DirectoryId == directoryId && x.Name == name && !x.IsDeleted, cancellationToken);
    }

    /// <summary>
    /// Проверяет, есть ли у владельца уже живая запись для данного файла в любой директории.
    /// Гарантирует инвариант «один блоб владельца — максимум одна запись в иерархии».
    /// Записи в корзине игнорируются: их можно вытеснить повторной загрузкой того же файла.
    /// </summary>
    public async Task<bool> FileEntryExistsForFile(long ownerId, Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.FileId == fileId && !x.IsDeleted, cancellationToken);
    }

    public async Task<CloudFileEntry> AddFileEntry(CloudFileEntry entry, CancellationToken cancellationToken = default)
    {
        _context.CloudFileEntries.Add(entry);
        await _context.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task UpdateFileEntry(CloudFileEntry entry, CancellationToken cancellationToken = default)
    {
        _context.CloudFileEntries.Update(entry);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveFileEntry(CloudFileEntry entry, CancellationToken cancellationToken = default)
    {
        _context.CloudFileEntries.Remove(entry);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<CloudFileEntry>> ListFilesInDirectory(long ownerId, Guid directoryId, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.DirectoryId == directoryId && !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Возвращает все живые CloudFileEntry, лежащие в любой из указанных директорий
    /// (записи в корзине исключаются — их не нужно перемещать в корзину повторно).
    /// </summary>
    public async Task<List<CloudFileEntry>> GetFileEntriesInDirectories(long ownerId, IReadOnlyCollection<Guid> directoryIds, CancellationToken cancellationToken = default)
    {
        if (directoryIds.Count == 0)
            return new List<CloudFileEntry>();

        return await _context.CloudFileEntries
            .Where(x => x.OwnerId == ownerId && directoryIds.Contains(x.DirectoryId) && !x.IsDeleted)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Живые (не в корзине) записи владельца для одного файла. Отслеживаемые —
    /// используются для мягкого удаления (мутация IsDeleted/DeletedAt/PurgeAt).
    /// </summary>
    public async Task<List<CloudFileEntry>> GetLiveEntriesForFile(long ownerId, Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .Where(e => e.OwnerId == ownerId && e.FileId == fileId && !e.IsDeleted)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Живые (не в корзине) записи владельца для набора файлов — для подсчёта копий в галерее.
    /// </summary>
    public async Task<List<CloudFileEntry>> GetLiveEntriesForFiles(long ownerId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        if (fileIds.Count == 0)
            return new List<CloudFileEntry>();

        return await _context.CloudFileEntries
            .AsNoTracking()
            .Where(e => e.OwnerId == ownerId && fileIds.Contains(e.FileId) && !e.IsDeleted)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Все записи владельца (включая корзину) для набора файлов — для устаревшего листинга изображений.
    /// </summary>
    public async Task<List<CloudFileEntry>> GetEntriesForFiles(long ownerId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        if (fileIds.Count == 0)
            return new List<CloudFileEntry>();

        return await _context.CloudFileEntries
            .AsNoTracking()
            .Where(e => e.OwnerId == ownerId && fileIds.Contains(e.FileId))
            .ToListAsync(cancellationToken);
    }

    public void RemoveFileEntries(IEnumerable<CloudFileEntry> entries)
    {
        _context.CloudFileEntries.RemoveRange(entries);
    }

    // ===== Корзина =====

    /// <summary>
    /// Трэш-запись (в корзине или нет) по идентификатору — для restore / purge.
    /// </summary>
    public async Task<CloudFileEntry?> GetTrashedEntry(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted, cancellationToken);
    }

    /// <summary>
    /// Страница записей в корзине владельца, отсортированных по (DeletedAt desc, Id desc),
    /// с cursor-пагинацией. Возвращает limit+1 элемент для определения наличия следующей страницы.
    /// </summary>
    public async Task<List<CloudFileEntry>> ListTrashedPage(
        long ownerId, DateTime? cursorDeletedAt, Guid? cursorEntryId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.CloudFileEntries
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.IsDeleted);

        if (cursorDeletedAt.HasValue && cursorEntryId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorDeletedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorEntryId.Value;
            query = query.Where(x =>
                x.DeletedAt < cursorAt
                || (x.DeletedAt == cursorAt && x.Id.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        return await query
            .OrderByDescending(x => x.DeletedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Все записи владельца в корзине (для «Очистить корзину»).
    /// </summary>
    public async Task<List<CloudFileEntry>> GetAllTrashedEntries(long ownerId, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .Where(x => x.OwnerId == ownerId && x.IsDeleted)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Пачка просроченных записей корзины (PurgeAt в прошлом) — для фонового воркера.
    /// </summary>
    public async Task<List<CloudFileEntry>> GetExpiredTrashedEntries(DateTime now, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .Where(x => x.IsDeleted && x.PurgeAt != null && x.PurgeAt <= now)
            .OrderBy(x => x.PurgeAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Из набора <paramref name="fileIds"/> возвращает те, что для владельца «эффективно в корзине»:
    /// есть запись в корзине и нет ни одной живой записи. Используется для скрытия таких файлов
    /// из галереи и альбомов.
    /// </summary>
    public async Task<HashSet<Guid>> GetEffectivelyTrashedFileIds(long ownerId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        if (fileIds.Count == 0)
            return new HashSet<Guid>();

        var states = await _context.CloudFileEntries
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId && fileIds.Contains(x.FileId))
            .Select(x => new { x.FileId, x.IsDeleted })
            .ToListAsync(cancellationToken);

        return states
            .GroupBy(x => x.FileId)
            .Where(g => g.Any(x => x.IsDeleted) && g.All(x => x.IsDeleted))
            .Select(g => g.Key)
            .ToHashSet();
    }

    /// <summary>
    /// Сохраняет все накопленные изменения в контексте.
    /// </summary>
    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }
}
