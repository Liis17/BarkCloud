using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

public class UploadedFilesStorage : IUploadedFilesStorage
{

    private readonly FilesContext _context;

    public UploadedFilesStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task<UploadFile> AddToStorage(UploadFile file)
    {
        _context.UploadedFiles.Add(file);

        await _context.SaveChangesAsync();

        return file;
    }

    public async Task UpdateFile(UploadFile file)
    {
        _context.UploadedFiles.Update(file);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Adds a user to the uploaders list if not already present.
    /// </summary>
    public async Task AddUploaderToFile(Guid fileId, long userId)
    {
        var file = await _context.UploadedFiles.FirstOrDefaultAsync(x => x.Id == fileId);
        if (file == null) return;

        if (!file.Uploaders.Contains(userId))
        {
            file.Uploaders.Add(userId);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<UploadFile?> GetFile(Guid id)
    {
        return await _context.UploadedFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<List<UploadFile>> GetFiles(List<Guid> ids)
    {
        return await _context.UploadedFiles
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToListAsync();
    }

    /// <summary>
    /// Deletes an UploadFile record by its ID (used for cleanup during deduplication).
    /// </summary>
    public async Task DeleteFile(Guid fileId)
    {
        var file = await _context.UploadedFiles.FirstOrDefaultAsync(x => x.Id == fileId);
        if (file != null)
        {
            _context.UploadedFiles.Remove(file);
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Находит оригинал, у которого есть превью с указанным идентификатором.
    /// Используется при отдаче превью по публичной ссылке (/download/{previewId}).
    /// </summary>
    public async Task<UploadFile?> GetOriginalByPreviewFileId(Guid previewFileId, CancellationToken cancellationToken = default)
    {
        var preview = await _context.FilePreviews
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PreviewFileId == previewFileId, cancellationToken);

        return preview is null
            ? null
            : await _context.UploadedFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == preview.OriginalFileId, cancellationToken);
    }

    /// <summary>
    /// Возвращает все превью оригинала.
    /// </summary>
    public async Task<List<FilePreview>> GetPreviewsForFile(Guid originalFileId, CancellationToken cancellationToken = default)
    {
        return await _context.FilePreviews
            .AsNoTracking()
            .Where(x => x.OriginalFileId == originalFileId)
            .OrderBy(x => x.TargetWidth)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Возвращает превью пачкой по списку оригиналов: ключ — OriginalFileId, значение — отсортированные превью.
    /// Оригиналы без превью в результате отсутствуют.
    /// </summary>
    public async Task<Dictionary<Guid, List<FilePreview>>> GetPreviewsForFiles(IEnumerable<Guid> originalFileIds, CancellationToken cancellationToken = default)
    {
        var ids = originalFileIds as IReadOnlyCollection<Guid> ?? originalFileIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, List<FilePreview>>();

        var previews = await _context.FilePreviews
            .AsNoTracking()
            .Where(x => ids.Contains(x.OriginalFileId))
            .ToListAsync(cancellationToken);

        return previews
            .GroupBy(x => x.OriginalFileId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(p => p.TargetWidth).ToList());
    }

    /// <summary>
    /// Снимает <paramref name="userId"/> из Uploaders файла (для декремента квоты).
    /// </summary>
    public async Task RemoveUploaderFromFile(Guid fileId, long userId, CancellationToken cancellationToken = default)
    {
        var file = await _context.UploadedFiles.FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (file is null) return;

        if (file.Uploaders.Remove(userId))
            await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the total storage used by a specific user in bytes.
    /// </summary>
    public async Task<long> GetUserStorageUsed(long userId)
    {
        return await _context.UploadedFiles
            .AsNoTracking()
            .Where(x => x.Uploaders.Contains(userId) && x.UploadedAt != null)
            .SumAsync(x => x.Size);
    }

    /// <summary>
    /// Gets storage usage by file type for a specific user.
    /// </summary>
    public async Task<Dictionary<UploadFileType, long>> GetUserStorageByType(long userId)
    {
        return await _context.UploadedFiles
            .AsNoTracking()
            .Where(x => x.Uploaders.Contains(userId) && x.UploadedAt != null)
            .GroupBy(x => x.Type)
            .Select(g => new { Type = g.Key, Size = g.Sum(x => x.Size) })
            .ToDictionaryAsync(x => x.Type, x => x.Size);
    }

    public async Task<bool> IsPreviewFile(Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.FilePreviews
            .AsNoTracking()
            .AnyAsync(p => p.PreviewFileId == fileId, cancellationToken);
    }

    /// <summary>
    /// Страница медиа владельца указанного <paramref name="kind"/> с cursor-пагинацией.
    /// Исключает превью-блобы и «эффективно удалённые» файлы (все записи владельца — в корзине).
    /// Возвращает limit+1 элемент для определения наличия следующей страницы.
    /// </summary>
    public async Task<List<UploadFile>> ListUserMediaPage(long ownerId, MediaKind kind, DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Uploaders.Contains(ownerId)
                        && f.Type == UploadFileType.CloudFile
                        && f.MediaKind == kind
                        && !_context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                        && !(_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && e.IsDeleted)
                             && !_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && !e.IsDeleted)));

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

    /// <summary>
    /// Страница «изображений» владельца с cursor-пагинацией (устаревший фильтр по расширению/ImageWidth,
    /// без явного MediaKind). Исключает превью-блобы и «эффективно удалённые» файлы.
    /// Возвращает limit+1 элемент для определения наличия следующей страницы.
    /// </summary>
    public async Task<List<UploadFile>> ListUserImagesPage(long ownerId, DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Uploaders.Contains(ownerId)
                        && f.Type == UploadFileType.CloudFile
                        && !_context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                        && !(_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && e.IsDeleted)
                             && !_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && !e.IsDeleted))
                        && (
                            (f.ImageWidth != null && f.ImageWidth > 0)
                            || (f.Filename != null && (
                                    f.Filename.ToLower().EndsWith(".jpg")
                                 || f.Filename.ToLower().EndsWith(".jpeg")
                                 || f.Filename.ToLower().EndsWith(".png")
                                 || f.Filename.ToLower().EndsWith(".gif")
                                 || f.Filename.ToLower().EndsWith(".webp")
                                 || f.Filename.ToLower().EndsWith(".heic")
                                 || f.Filename.ToLower().EndsWith(".heif")
                                 || f.Filename.ToLower().EndsWith(".bmp")
                                 || f.Filename.ToLower().EndsWith(".tiff")
                                 || f.Filename.ToLower().EndsWith(".tif")))
                        ));

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

    public async Task<List<MemoryMediaItem>> ListMemoriesForDay(long ownerId, int month, int day, int maxTotal, CancellationToken cancellationToken = default)
    {
        // Базовый фильтр «живых» медиа владельца (как ListUserMediaPage), плюс join к метаданным
        // по дате съёмки. Сравнение по месяцу/дню Npgsql транслирует в date_part('month'/'day').
        var query =
            from f in _context.UploadedFiles.AsNoTracking()
            join m in _context.FileMetadata on f.Id equals m.FileId
            where f.Uploaders.Contains(ownerId)
                  && f.Type == UploadFileType.CloudFile
                  && (f.MediaKind == MediaKind.Photo || f.MediaKind == MediaKind.Video)
                  && m.TakenAt != null
                  && m.TakenAt.Value.Month == month
                  && m.TakenAt.Value.Day == day
                  && !_context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                  && !(_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && e.IsDeleted)
                       && !_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && !e.IsDeleted))
            orderby m.TakenAt descending
            select new { File = f, m.TakenAt };

        var rows = await query.Take(maxTotal).ToListAsync(cancellationToken);

        return rows
            .Select(x => new MemoryMediaItem(x.File, x.TakenAt!.Value))
            .ToList();
    }

    public async Task<List<LocatedMediaItem>> ListMediaWithLocationPage(long ownerId, DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query =
            from f in _context.UploadedFiles.AsNoTracking()
            join m in _context.FileMetadata on f.Id equals m.FileId
            where f.Uploaders.Contains(ownerId)
                  && f.Type == UploadFileType.CloudFile
                  && (f.MediaKind == MediaKind.Photo || f.MediaKind == MediaKind.Video)
                  && m.Latitude != null
                  && m.Longitude != null
                  && !_context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                  && !(_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && e.IsDeleted)
                       && !_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && !e.IsDeleted))
            select new { File = f, m.Latitude, m.Longitude, m.TakenAt };

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

        return rows
            .Select(x => new LocatedMediaItem(x.File, x.Latitude!.Value, x.Longitude!.Value, x.TakenAt))
            .ToList();
    }

    /// <summary>
    /// Снимает все превью оригинала: убирает владельца из Uploaders превью-блобов и удаляет
    /// записи FilePreview. Используется при ручной смене превью видео.
    /// </summary>
    public async Task RemovePreviewsForOriginal(Guid originalFileId, long ownerId, CancellationToken cancellationToken = default)
    {
        var oldPreviews = await _context.FilePreviews
            .Where(p => p.OriginalFileId == originalFileId)
            .ToListAsync(cancellationToken);

        if (oldPreviews.Count == 0)
            return;

        var oldPreviewFileIds = oldPreviews.Select(p => p.PreviewFileId).ToList();
        var oldPreviewFiles = await _context.UploadedFiles
            .Where(f => oldPreviewFileIds.Contains(f.Id))
            .ToListAsync(cancellationToken);

        foreach (var pf in oldPreviewFiles)
            pf.Uploaders.Remove(ownerId);

        _context.FilePreviews.RemoveRange(oldPreviews);
        await _context.SaveChangesAsync(cancellationToken);
    }
}