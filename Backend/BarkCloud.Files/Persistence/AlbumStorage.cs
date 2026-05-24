using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище альбомов пользователя и их элементов.
/// </summary>
public class AlbumStorage
{
    private readonly FilesContext _context;

    public AlbumStorage(FilesContext context)
    {
        _context = context;
    }

    // ===== Albums =====

    public async Task<Album?> GetAlbum(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Albums
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<bool> AlbumNameExists(long ownerId, string name, CancellationToken cancellationToken = default)
    {
        return await _context.Albums
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.Name == name, cancellationToken);
    }

    public async Task<Album> AddAlbum(Album album, CancellationToken cancellationToken = default)
    {
        _context.Albums.Add(album);
        await _context.SaveChangesAsync(cancellationToken);
        return album;
    }

    public async Task UpdateAlbum(Album album, CancellationToken cancellationToken = default)
    {
        _context.Albums.Update(album);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAlbum(Album album, CancellationToken cancellationToken = default)
    {
        var items = await _context.AlbumItems
            .Where(x => x.AlbumId == album.Id)
            .ToListAsync(cancellationToken);

        _context.AlbumItems.RemoveRange(items);
        _context.Albums.Remove(album);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Страница альбомов владельца, отсортированных по (UpdatedAt desc, Id desc), с cursor-пагинацией.
    /// Возвращает limit+1 элемент для определения наличия следующей страницы.
    /// </summary>
    public async Task<List<Album>> ListAlbumsPage(
        long ownerId, DateTime? cursorUpdatedAt, Guid? cursorAlbumId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.Albums
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId);

        if (cursorUpdatedAt.HasValue && cursorAlbumId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorUpdatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorAlbumId.Value;
            query = query.Where(x =>
                x.UpdatedAt < cursorAt
                || (x.UpdatedAt == cursorAt && x.Id.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        return await query
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
    }

    // ===== Album items =====

    /// <summary>
    /// Количество элементов в каждом из указанных альбомов.
    /// </summary>
    public async Task<Dictionary<Guid, int>> GetItemCounts(IEnumerable<Guid> albumIds, CancellationToken cancellationToken = default)
    {
        var ids = albumIds as IReadOnlyCollection<Guid> ?? albumIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, int>();

        var counts = await _context.AlbumItems
            .AsNoTracking()
            .Where(x => ids.Contains(x.AlbumId))
            .GroupBy(x => x.AlbumId)
            .Select(g => new { AlbumId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.AlbumId, x => x.Count);
    }

    /// <summary>
    /// Существующие FileId в альбоме из заданного набора (для пропуска дублей при добавлении).
    /// </summary>
    public async Task<HashSet<Guid>> GetExistingItemFileIds(Guid albumId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        if (fileIds.Count == 0)
            return new HashSet<Guid>();

        var existing = await _context.AlbumItems
            .AsNoTracking()
            .Where(x => x.AlbumId == albumId && fileIds.Contains(x.FileId))
            .Select(x => x.FileId)
            .ToListAsync(cancellationToken);

        return existing.ToHashSet();
    }

    public async Task AddItems(IEnumerable<AlbumItem> items, CancellationToken cancellationToken = default)
    {
        _context.AlbumItems.AddRange(items);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RemoveItems(Guid albumId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        if (fileIds.Count == 0)
            return 0;

        var items = await _context.AlbumItems
            .Where(x => x.AlbumId == albumId && fileIds.Contains(x.FileId))
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
            return 0;

        _context.AlbumItems.RemoveRange(items);
        await _context.SaveChangesAsync(cancellationToken);
        return items.Count;
    }

    /// <summary>
    /// Страница элементов альбома, отсортированных по (AddedAt desc, FileId desc), с cursor-пагинацией.
    /// Возвращает limit+1 элемент для определения наличия следующей страницы.
    /// </summary>
    public async Task<List<AlbumItem>> ListItemsPage(
        Guid albumId, DateTime? cursorAddedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.AlbumItems
            .AsNoTracking()
            .Where(x => x.AlbumId == albumId);

        if (cursorAddedAt.HasValue && cursorFileId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorAddedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorFileId.Value;
            query = query.Where(x =>
                x.AddedAt < cursorAt
                || (x.AddedAt == cursorAt && x.FileId.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        return await query
            .OrderByDescending(x => x.AddedAt)
            .ThenByDescending(x => x.FileId)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Первый (самый ранний) элемент альбома — кандидат на авто-обложку.
    /// </summary>
    public async Task<AlbumItem?> GetFirstItem(Guid albumId, CancellationToken cancellationToken = default)
    {
        return await _context.AlbumItems
            .AsNoTracking()
            .Where(x => x.AlbumId == albumId)
            .OrderBy(x => x.AddedAt)
            .ThenBy(x => x.FileId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
