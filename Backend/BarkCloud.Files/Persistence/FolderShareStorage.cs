using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище публичных папок (<see cref="FolderShareLink"/>). Резолв по токену анонимный,
/// листинг/отзыв — в рамках владельца.
/// </summary>
public class FolderShareStorage : IFolderShareStorage
{
    private readonly FilesContext _context;

    public FolderShareStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task Add(FolderShareLink item, CancellationToken cancellationToken = default)
    {
        _context.FolderShareLinks.Add(item);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<FolderShareLink?> GetByToken(string token, CancellationToken cancellationToken = default)
    {
        return await _context.FolderShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);
    }

    public async Task<FolderShareLink?> GetByDirectory(long ownerId, Guid directoryId, CancellationToken cancellationToken = default)
    {
        return await _context.FolderShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.DirectoryId == directoryId, cancellationToken);
    }

    public async Task<FolderShareLink?> GetById(long ownerId, Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.FolderShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.Id == id, cancellationToken);
    }

    /// <summary>Отозвать публичность папки владельцем. Идемпотентно (0, если строки не было / не его).</summary>
    public async Task<int> Remove(long ownerId, Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.FolderShareLinks
            .Where(x => x.OwnerId == ownerId && x.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Снять публичность с набора папок владельца (при удалении папки/поддерева).</summary>
    public async Task<int> RemoveByDirectories(long ownerId, IReadOnlyCollection<Guid> directoryIds, CancellationToken cancellationToken = default)
    {
        if (directoryIds.Count == 0)
            return 0;

        return await _context.FolderShareLinks
            .Where(x => x.OwnerId == ownerId && directoryIds.Contains(x.DirectoryId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Удалить все публичные папки владельца (при удалении аккаунта).</summary>
    public async Task<int> RemoveForUser(long userId, CancellationToken cancellationToken = default)
    {
        return await _context.FolderShareLinks
            .Where(x => x.OwnerId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Атомарно увеличить счётчик открытий публичной папки.</summary>
    public async Task IncrementClicks(Guid id, CancellationToken cancellationToken = default)
    {
        await _context.FolderShareLinks
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClickCount, x => x.ClickCount + 1), cancellationToken);
    }

    /// <summary>
    /// Страница публичных папок владельца по (CreatedAt desc, Id desc), cursor-пагинация.
    /// Возвращает limit+1 для определения следующей страницы.
    /// </summary>
    public async Task<List<FolderShareLink>> ListPage(
        long ownerId, DateTime? cursorCreatedAt, Guid? cursorId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.FolderShareLinks
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
