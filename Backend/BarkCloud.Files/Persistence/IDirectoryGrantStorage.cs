using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IDirectoryGrantStorage
{
    Task Add(DirectoryGrant grant, CancellationToken cancellationToken = default);
    Task<bool> Exists(long ownerId, Guid directoryId, long recipientId, CancellationToken cancellationToken = default);
    Task<DirectoryGrant?> GetById(Guid grantId, CancellationToken cancellationToken = default);
    Task<int> Remove(long ownerId, Guid grantId, CancellationToken cancellationToken = default);
    Task<List<DirectoryGrant>> ListByRecipient(long recipientId, CancellationToken cancellationToken = default);
    Task<List<DirectoryGrant>> ListByOwner(long ownerId, CancellationToken cancellationToken = default);
    Task<int> RemoveByDirectories(long ownerId, IReadOnlyCollection<Guid> directoryIds, CancellationToken cancellationToken = default);
    Task<int> RemoveForUser(long userId, CancellationToken cancellationToken = default);
}
