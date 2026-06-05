using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище публичных альбомов (<see cref="AlbumShareLink"/>). Резолв по токену анонимный,
/// листинг/отзыв — в рамках владельца.
/// </summary>
public class AlbumShareStorage : IAlbumShareStorage
{
    private readonly FilesContext _context;

    public AlbumShareStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task Add(AlbumShareLink item, CancellationToken cancellationToken = default)
    {
        _context.AlbumShareLinks.Add(item);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AlbumShareLink?> GetByToken(string token, CancellationToken cancellationToken = default)
    {
        return await _context.AlbumShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);
    }

    public async Task<AlbumShareLink?> GetByAlbum(long ownerId, Guid albumId, CancellationToken cancellationToken = default)
    {
        return await _context.AlbumShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.AlbumId == albumId, cancellationToken);
    }

    public async Task<AlbumShareLink?> GetById(long ownerId, Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.AlbumShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.Id == id, cancellationToken);
    }

    /// <summary>Отозвать публичность альбома владельцем. Идемпотентно (0, если строки не было / не его).</summary>
    public async Task<int> Remove(long ownerId, Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.AlbumShareLinks
            .Where(x => x.OwnerId == ownerId && x.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Снять публичность с альбома (при удалении альбома).</summary>
    public async Task<int> RemoveByAlbum(long ownerId, Guid albumId, CancellationToken cancellationToken = default)
    {
        return await _context.AlbumShareLinks
            .Where(x => x.OwnerId == ownerId && x.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Удалить все публичные альбомы владельца (при удалении аккаунта).</summary>
    public async Task<int> RemoveForUser(long userId, CancellationToken cancellationToken = default)
    {
        return await _context.AlbumShareLinks
            .Where(x => x.OwnerId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Атомарно увеличить счётчик открытий публичного альбома.</summary>
    public async Task IncrementClicks(Guid id, CancellationToken cancellationToken = default)
    {
        await _context.AlbumShareLinks
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClickCount, x => x.ClickCount + 1), cancellationToken);
    }

    /// <summary>
    /// Страница публичных альбомов владельца по (CreatedAt desc, Id desc), cursor-пагинация.
    /// Возвращает limit+1 для определения следующей страницы.
    /// </summary>
    public async Task<List<AlbumShareLink>> ListPage(
        long ownerId, DateTime? cursorCreatedAt, Guid? cursorId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.AlbumShareLinks
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId);

        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cid = cursorId.Value;
            query = query.Where(x =>
                x.CreatedAt < cursorAt
                || (x.CreatedAt == cursorAt && x.Id.ToString().CompareTo(cid.ToString()) < 0));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
    }
}
