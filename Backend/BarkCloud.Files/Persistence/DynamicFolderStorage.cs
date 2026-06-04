using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище пользовательских умных папок и вычисление их содержимого по критериям
/// (через <see cref="DynamicFolderQueryBuilder"/>). Системные папки здесь не хранятся.
/// </summary>
public class DynamicFolderStorage : IDynamicFolderStorage
{
    private readonly FilesContext _context;

    public DynamicFolderStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task<DynamicFolder?> GetFolder(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.DynamicFolders
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<bool> FolderNameExists(long ownerId, string name, CancellationToken cancellationToken = default)
    {
        return await _context.DynamicFolders
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.Name == name, cancellationToken);
    }

    public async Task<DynamicFolder> AddFolder(DynamicFolder folder, CancellationToken cancellationToken = default)
    {
        _context.DynamicFolders.Add(folder);
        await _context.SaveChangesAsync(cancellationToken);
        return folder;
    }

    public async Task UpdateFolder(DynamicFolder folder, CancellationToken cancellationToken = default)
    {
        _context.DynamicFolders.Update(folder);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveFolder(DynamicFolder folder, CancellationToken cancellationToken = default)
    {
        _context.DynamicFolders.Remove(folder);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<DynamicFolder>> ListFolders(long ownerId, CancellationToken cancellationToken = default)
    {
        return await _context.DynamicFolders
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetMaxSortOrder(long ownerId, CancellationToken cancellationToken = default)
    {
        var max = await _context.DynamicFolders
            .Where(x => x.OwnerId == ownerId)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync(cancellationToken);
        return max ?? -1;
    }

    public async Task<int> CountByCriteria(long ownerId, DynamicFolderCriteria criteria, DateTime now, CancellationToken cancellationToken = default)
    {
        return await DynamicFolderQueryBuilder
            .BuildQuery(_context, ownerId, criteria, now)
            .CountAsync(cancellationToken);
    }

    public async Task<List<UploadFile>> ListItemsPage(
        long ownerId, DynamicFolderCriteria criteria, DateTime now,
        DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query = DynamicFolderQueryBuilder.BuildQuery(_context, ownerId, criteria, now);

        if (cursorCreatedAt.HasValue && cursorFileId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorFileId.Value;
            query = query.Where(f =>
                f.CreatedAt < cursorAt
                || (f.CreatedAt == cursorAt && f.Id.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        return await query
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
    }

    public async Task<UploadFile?> GetFirstItem(long ownerId, DynamicFolderCriteria criteria, DateTime now, CancellationToken cancellationToken = default)
    {
        return await DynamicFolderQueryBuilder
            .BuildQuery(_context, ownerId, criteria, now)
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
