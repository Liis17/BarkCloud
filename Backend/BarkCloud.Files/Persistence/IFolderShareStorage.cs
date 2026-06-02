using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IFolderShareStorage
{
    Task Add(FolderShareLink item, CancellationToken cancellationToken = default);
    Task<FolderShareLink?> GetByToken(string token, CancellationToken cancellationToken = default);
    Task<FolderShareLink?> GetByDirectory(long ownerId, Guid directoryId, CancellationToken cancellationToken = default);
    Task<FolderShareLink?> GetById(long ownerId, Guid id, CancellationToken cancellationToken = default);
    Task<int> Remove(long ownerId, Guid id, CancellationToken cancellationToken = default);
    Task<int> RemoveByDirectories(long ownerId, IReadOnlyCollection<Guid> directoryIds, CancellationToken cancellationToken = default);
    Task<int> RemoveForUser(long userId, CancellationToken cancellationToken = default);
    Task IncrementClicks(Guid id, CancellationToken cancellationToken = default);
    Task<List<FolderShareLink>> ListPage(long ownerId, DateTime? cursorCreatedAt, Guid? cursorId, int limit, CancellationToken cancellationToken = default);
}
