using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IShareStorage
{
    Task Add(ShareLink item, CancellationToken cancellationToken = default);
    Task<ShareLink?> GetByToken(string token, CancellationToken cancellationToken = default);
    Task<int> Remove(long ownerId, Guid shareId, CancellationToken cancellationToken = default);
    Task IncrementClicks(Guid id, CancellationToken cancellationToken = default);
    Task<List<ShareLink>> ListPage(long ownerId, DateTime? cursorCreatedAt, Guid? cursorShareId, int limit, CancellationToken cancellationToken = default);
}
