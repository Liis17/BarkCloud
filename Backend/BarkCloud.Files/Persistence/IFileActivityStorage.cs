using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IFileActivityStorage
{
    Task Add(FileActivityEvent activity, CancellationToken cancellationToken = default);

    Task AddRange(IEnumerable<FileActivityEvent> activities, CancellationToken cancellationToken = default);

    Task<List<FileActivityEvent>> ListPage(
        long ownerId,
        Guid fileId,
        DateTime? cursorCreatedAt,
        Guid? cursorEventId,
        int limit,
        CancellationToken cancellationToken = default);
}
