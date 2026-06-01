using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IGrantStorage
{
    Task Add(FileGrant grant, CancellationToken cancellationToken = default);
    Task<bool> Exists(long ownerId, Guid fileId, long recipientId, CancellationToken cancellationToken = default);
    Task<bool> RecipientHasAccess(long recipientId, Guid fileId, CancellationToken cancellationToken = default);
    Task<FileGrant?> GetById(Guid grantId, CancellationToken cancellationToken = default);
    Task<int> Remove(long ownerId, Guid grantId, CancellationToken cancellationToken = default);
    Task<List<FileGrant>> ListSharedWithMePage(long recipientId, DateTime? cursorCreatedAt, Guid? cursorGrantId, int limit, CancellationToken cancellationToken = default);
    Task<List<FileGrant>> ListByOwnerFile(long ownerId, Guid fileId, CancellationToken cancellationToken = default);
    Task<int> RemoveByFile(long ownerId, Guid fileId, CancellationToken cancellationToken = default);
    Task<int> RemoveForUser(long userId, CancellationToken cancellationToken = default);
}
