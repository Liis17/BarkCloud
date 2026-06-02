using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище грантов доступа к папкам (<see cref="DirectoryGrant"/>): шаринг папки пользователям.
/// </summary>
public class DirectoryGrantStorage : IDirectoryGrantStorage
{
    private readonly FilesContext _context;

    public DirectoryGrantStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task Add(DirectoryGrant grant, CancellationToken cancellationToken = default)
    {
        _context.DirectoryGrants.Add(grant);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> Exists(long ownerId, Guid directoryId, long recipientId, CancellationToken cancellationToken = default)
    {
        return await _context.DirectoryGrants
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.DirectoryId == directoryId && x.RecipientId == recipientId, cancellationToken);
    }

    public async Task<DirectoryGrant?> GetById(Guid grantId, CancellationToken cancellationToken = default)
    {
        return await _context.DirectoryGrants
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == grantId, cancellationToken);
    }

    /// <summary>Отозвать грант владельцем. Идемпотентно (0, если строки не было / не его).</summary>
    public async Task<int> Remove(long ownerId, Guid grantId, CancellationToken cancellationToken = default)
    {
        return await _context.DirectoryGrants
            .Where(x => x.OwnerId == ownerId && x.Id == grantId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Все папки, доступные получателю (от новых к старым).</summary>
    public async Task<List<DirectoryGrant>> ListByRecipient(long recipientId, CancellationToken cancellationToken = default)
    {
        return await _context.DirectoryGrants
            .AsNoTracking()
            .Where(x => x.RecipientId == recipientId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Все исходящие гранты владельца на папки (для «я поделился»).</summary>
    public async Task<List<DirectoryGrant>> ListByOwner(long ownerId, CancellationToken cancellationToken = default)
    {
        return await _context.DirectoryGrants
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Снять гранты владельца на набор папок (при удалении папки/поддерева).</summary>
    public async Task<int> RemoveByDirectories(long ownerId, IReadOnlyCollection<Guid> directoryIds, CancellationToken cancellationToken = default)
    {
        if (directoryIds.Count == 0)
            return 0;

        return await _context.DirectoryGrants
            .Where(x => x.OwnerId == ownerId && directoryIds.Contains(x.DirectoryId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Удалить все гранты, где пользователь — владелец ИЛИ получатель (при удалении аккаунта).</summary>
    public async Task<int> RemoveForUser(long userId, CancellationToken cancellationToken = default)
    {
        return await _context.DirectoryGrants
            .Where(x => x.OwnerId == userId || x.RecipientId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
