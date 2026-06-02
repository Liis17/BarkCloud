using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище грантов доступа (<see cref="FileGrant"/>): шаринг файла конкретным пользователям.
/// Листинг «мне доступны» строго в рамках получателя.
/// </summary>
public class GrantStorage : IGrantStorage
{
    private readonly FilesContext _context;

    public GrantStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task Add(FileGrant grant, CancellationToken cancellationToken = default)
    {
        _context.FileGrants.Add(grant);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> Exists(long ownerId, Guid fileId, long recipientId, CancellationToken cancellationToken = default)
    {
        return await _context.FileGrants
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.FileId == fileId && x.RecipientId == recipientId, cancellationToken);
    }

    /// <summary>Есть ли у получателя доступ к файлу (любой владелец) — для проверки скачивания.</summary>
    public async Task<bool> RecipientHasAccess(long recipientId, Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.FileGrants
            .AsNoTracking()
            .AnyAsync(x => x.RecipientId == recipientId && x.FileId == fileId, cancellationToken);
    }

    public async Task<FileGrant?> GetById(Guid grantId, CancellationToken cancellationToken = default)
    {
        return await _context.FileGrants
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == grantId, cancellationToken);
    }

    /// <summary>Отозвать грант владельцем. Идемпотентно (0, если строки не было / не его).</summary>
    public async Task<int> Remove(long ownerId, Guid grantId, CancellationToken cancellationToken = default)
    {
        return await _context.FileGrants
            .Where(x => x.OwnerId == ownerId && x.Id == grantId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Страница «мне доступны» получателя по (CreatedAt desc, Id desc) с cursor-пагинацией.
    /// Возвращает limit+1 элемент для определения наличия следующей страницы.
    /// </summary>
    public async Task<List<FileGrant>> ListSharedWithMePage(
        long recipientId, DateTime? cursorCreatedAt, Guid? cursorGrantId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.FileGrants
            .AsNoTracking()
            .Where(x => x.RecipientId == recipientId);

        if (cursorCreatedAt.HasValue && cursorGrantId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorGrantId.Value;
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

    /// <summary>
    /// Страница «я поделился» владельца по (CreatedAt desc, Id desc) с cursor-пагинацией.
    /// Все исходящие гранты владельца (по всем файлам). Возвращает limit+1 для определения следующей страницы.
    /// </summary>
    public async Task<List<FileGrant>> ListByOwnerPage(
        long ownerId, DateTime? cursorCreatedAt, Guid? cursorGrantId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.FileGrants
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId);

        if (cursorCreatedAt.HasValue && cursorGrantId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorGrantId.Value;
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

    /// <summary>Гранты владельца на конкретный файл (с кем поделено) — для управления.</summary>
    public async Task<List<FileGrant>> ListByOwnerFile(long ownerId, Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.FileGrants
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.FileId == fileId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Удалить все гранты владельца на файл (при окончательном удалении файла).</summary>
    public async Task<int> RemoveByFile(long ownerId, Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.FileGrants
            .Where(x => x.OwnerId == ownerId && x.FileId == fileId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Удалить все гранты, где пользователь — владелец ИЛИ получатель (при удалении аккаунта).</summary>
    public async Task<int> RemoveForUser(long userId, CancellationToken cancellationToken = default)
    {
        return await _context.FileGrants
            .Where(x => x.OwnerId == userId || x.RecipientId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
