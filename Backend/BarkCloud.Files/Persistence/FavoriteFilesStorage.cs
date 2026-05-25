using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Хранилище избранных файлов пользователя. Структурно повторяет item-методы
/// <see cref="AlbumStorage"/>, но без привязки к альбому.
/// </summary>
public class FavoriteFilesStorage
{
    private readonly FilesContext _context;

    public FavoriteFilesStorage(FilesContext context)
    {
        _context = context;
    }

    /// <summary>Файл уже в избранном владельца — для идемпотентного добавления.</summary>
    public async Task<bool> Exists(long ownerId, Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.FavoriteFiles
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.FileId == fileId, cancellationToken);
    }

    public async Task Add(FavoriteFile item, CancellationToken cancellationToken = default)
    {
        _context.FavoriteFiles.Add(item);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Убрать файл из избранного владельца. Идемпотентно (0, если строки не было).</summary>
    public async Task<int> Remove(long ownerId, Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.FavoriteFiles
            .Where(x => x.OwnerId == ownerId && x.FileId == fileId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Страница избранного владельца, отсортированная по (CreatedAt desc, FileId desc), с cursor-пагинацией.
    /// Возвращает limit+1 элемент для определения наличия следующей страницы.
    /// </summary>
    public async Task<List<FavoriteFile>> ListPage(
        long ownerId, DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.FavoriteFiles
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId);

        if (cursorCreatedAt.HasValue && cursorFileId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorFileId.Value;
            query = query.Where(x =>
                x.CreatedAt < cursorAt
                || (x.CreatedAt == cursorAt && x.FileId.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.FileId)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
    }
}
