using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

public class UploadedFilesStorage
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
}