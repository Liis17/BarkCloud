using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище иерархии облачных папок и файловых записей пользователя.
/// Корневая папка не материализуется — её представляет ParentId == null / DirectoryId == null
/// в запросах, но для CloudFileEntry мы используем выделенный синтетический Guid.Empty
/// в качестве идентификатора корневой папки на уровне хранения.
/// </summary>
public class CloudHierarchyStorage
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

    public async Task<bool> FileEntryNameExists(long ownerId, Guid directoryId, string name, CancellationToken cancellationToken = default)
    {
        return await _context.CloudFileEntries
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.DirectoryId == directoryId && x.Name == name, cancellationToken);
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
            .Where(x => x.OwnerId == ownerId && x.DirectoryId == directoryId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Возвращает все CloudFileEntry, лежащие в любой из указанных директорий.
    /// </summary>
    public async Task<List<CloudFileEntry>> GetFileEntriesInDirectories(long ownerId, IReadOnlyCollection<Guid> directoryIds, CancellationToken cancellationToken = default)
    {
        if (directoryIds.Count == 0)
            return new List<CloudFileEntry>();

        return await _context.CloudFileEntries
            .Where(x => x.OwnerId == ownerId && directoryIds.Contains(x.DirectoryId))
            .ToListAsync(cancellationToken);
    }

    public void RemoveFileEntries(IEnumerable<CloudFileEntry> entries)
    {
        _context.CloudFileEntries.RemoveRange(entries);
    }

    /// <summary>
    /// Сохраняет все накопленные изменения в контексте.
    /// </summary>
    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }
}
