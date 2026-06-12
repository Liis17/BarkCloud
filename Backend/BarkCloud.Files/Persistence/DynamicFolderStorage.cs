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

    public async Task<int> CountDuplicateItems(long ownerId, bool mediaOnly, CancellationToken cancellationToken = default)
    {
        var query = BuildDuplicateItemsQuery(ownerId, mediaOnly);
        return await query.CountAsync(cancellationToken);
    }

    public async Task<List<DuplicateFileItem>> ListDuplicateItemsPage(
        long ownerId, bool mediaOnly,
        DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query = BuildDuplicateItemsQuery(ownerId, mediaOnly);

        if (cursorCreatedAt.HasValue && cursorFileId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorFileId.Value;
            query = query.Where(x =>
                x.File.CreatedAt < cursorAt
                || (x.File.CreatedAt == cursorAt && x.File.Id.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        var rows = await query
            .OrderByDescending(x => x.File.CreatedAt)
            .ThenByDescending(x => x.File.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new DuplicateFileItem(x.File, x.Hash)).ToList();
    }

    public async Task<UploadFile?> GetFirstDuplicateItem(long ownerId, bool mediaOnly, CancellationToken cancellationToken = default)
    {
        return await BuildDuplicateItemsQuery(ownerId, mediaOnly)
            .OrderByDescending(x => x.File.CreatedAt)
            .ThenByDescending(x => x.File.Id)
            .Select(x => x.File)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private IQueryable<DuplicateFileRow> BuildDuplicateItemsQuery(long ownerId, bool mediaOnly)
    {
        var baseQuery = DynamicFolderQueryBuilder.BuildBaseQuery(_context, ownerId);
        if (mediaOnly)
            baseQuery = baseQuery.Where(f => f.MediaKind == MediaKind.Photo || f.MediaKind == MediaKind.Video);
        else
            baseQuery = baseQuery.Where(f => f.MediaKind != MediaKind.Photo && f.MediaKind != MediaKind.Video);

        var duplicateHashes =
            from h in _context.FileHashes.AsNoTracking()
            join f in baseQuery on h.FileId equals f.Id
            group h by h.Hash into g
            where g.Count() > 1
            select g.Key;

        return
            from f in baseQuery
            join h in _context.FileHashes.AsNoTracking() on f.Id equals h.FileId
            where duplicateHashes.Contains(h.Hash)
            select new DuplicateFileRow { File = f, Hash = h.Hash };
    }

    private sealed class DuplicateFileRow
    {
        public UploadFile File { get; init; } = null!;
        public string Hash { get; init; } = string.Empty;
    }
}
