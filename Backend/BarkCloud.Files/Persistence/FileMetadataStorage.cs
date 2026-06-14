using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

public class FileMetadataStorage : IFileMetadataStorage
{
    private readonly FilesContext _context;

    public FileMetadataStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task<FileMetadata?> Get(Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _context.FileMetadata
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.FileId == fileId, cancellationToken);
    }

    public async Task<Dictionary<Guid, FileMetadata>> GetForFiles(
        IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        if (fileIds.Count == 0)
            return new Dictionary<Guid, FileMetadata>();

        return await _context.FileMetadata
            .AsNoTracking()
            .Where(m => fileIds.Contains(m.FileId))
            .ToDictionaryAsync(m => m.FileId, cancellationToken);
    }

    public async Task<List<Guid>> ListVideosMissingHdr(
        Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.FileMetadata
            .AsNoTracking()
            .Where(m => m.IsHdr == null)
            .Join(_context.UploadedFiles.AsNoTracking(),
                m => m.FileId, f => f.Id, (m, f) => f)
            .Where(f => f.MediaKind == MediaKind.Video && !string.IsNullOrEmpty(f.Etag));

        if (cursorFileId.HasValue)
        {
            var cursor = cursorFileId.Value;
            query = query.Where(f => f.Id.ToString().CompareTo(cursor.ToString()) > 0);
        }

        return await query
            .OrderBy(f => f.Id)
            .Select(f => f.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task SetHdr(Guid fileId, bool isHdr, CancellationToken cancellationToken = default)
    {
        await _context.FileMetadata
            .Where(x => x.FileId == fileId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHdr, isHdr), cancellationToken);
    }

    public async Task AddIfMissing(FileMetadata metadata, CancellationToken cancellationToken = default)
    {
        var exists = await _context.FileMetadata
            .AsNoTracking()
            .AnyAsync(x => x.FileId == metadata.FileId, cancellationToken);

        if (exists)
            return;

        _context.FileMetadata.Add(metadata);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<Guid>> ListFilesMissingMetadata(
        Guid? cursorFileId, int limit, CancellationToken cancellationToken = default)
    {
        var query = _context.UploadedFiles
            .AsNoTracking()
            .Where(f => !string.IsNullOrEmpty(f.Etag))
            .Where(f => !_context.FileMetadata.Any(m => m.FileId == f.Id));

        if (cursorFileId.HasValue)
        {
            var cursor = cursorFileId.Value;
            query = query.Where(f => f.Id.ToString().CompareTo(cursor.ToString()) > 0);
        }

        return await query
            .OrderBy(f => f.Id)
            .Select(f => f.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
