using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

public class FileActivityStorage : IFileActivityStorage
{
    private readonly FilesContext _context;

    public FileActivityStorage(FilesContext context)
    {
        _context = context;
    }

    public async Task Add(FileActivityEvent activity, CancellationToken cancellationToken = default)
    {
        _context.FileActivityEvents.Add(activity);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRange(IEnumerable<FileActivityEvent> activities, CancellationToken cancellationToken = default)
    {
        _context.FileActivityEvents.AddRange(activities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<FileActivityEvent>> ListPage(
        long ownerId,
        Guid fileId,
        DateTime? cursorCreatedAt,
        Guid? cursorEventId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = _context.FileActivityEvents
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.FileId == fileId);

        if (cursorCreatedAt.HasValue && cursorEventId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorEventId.Value;
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
