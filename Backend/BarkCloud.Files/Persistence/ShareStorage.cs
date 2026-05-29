using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище публичных ссылок (<see cref="ShareLink"/>). Резолв по токену анонимный,
/// листинг и отзыв — в рамках владельца.
/// </summary>
public class ShareStorage : IShareStorage
{
    private readonly FilesContext _context;

    public ShareStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task Add(ShareLink item, CancellationToken cancellationToken = default)
    {
        _context.ShareLinks.Add(item);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ShareLink?> GetByToken(string token, CancellationToken cancellationToken = default)
    {
        return await _context.ShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);
    }

    /// <summary>Отозвать ссылку владельца. Идемпотентно (0, если строки не было / не его).</summary>
    public async Task<int> Remove(long ownerId, Guid shareId, CancellationToken cancellationToken = default)
    {
        return await _context.ShareLinks
            .Where(x => x.OwnerId == ownerId && x.Id == shareId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Атомарно увеличить счётчик переходов по ссылке.</summary>
    public async Task IncrementClicks(Guid id, CancellationToken cancellationToken = default)
    {
        await _context.ShareLinks
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClickCount, x => x.ClickCount + 1), cancellationToken);
    }

    /// <summary>
    /// Страница ссылок владельца, отсортированная по (CreatedAt desc, Id desc), с cursor-пагинацией.
    /// Возвращает limit+1 элемент для определения наличия следующей страницы.
    /// </summary>
    public async Task<List<ShareLink>> ListPage(
        long ownerId, DateTime? cursorCreatedAt, Guid? cursorShareId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.ShareLinks
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId);

        if (cursorCreatedAt.HasValue && cursorShareId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorShareId.Value;
            query = query.Where(x =>
                x.CreatedAt < cursorAt
                || (x.CreatedAt == cursorAt && x.Id.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
    }
}
