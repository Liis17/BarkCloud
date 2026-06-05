using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IAlbumShareStorage
{
    Task Add(AlbumShareLink item, CancellationToken cancellationToken = default);
    Task<AlbumShareLink?> GetByToken(string token, CancellationToken cancellationToken = default);
    Task<AlbumShareLink?> GetByAlbum(long ownerId, Guid albumId, CancellationToken cancellationToken = default);
    Task<AlbumShareLink?> GetById(long ownerId, Guid id, CancellationToken cancellationToken = default);
    Task<int> Remove(long ownerId, Guid id, CancellationToken cancellationToken = default);
    Task<int> RemoveByAlbum(long ownerId, Guid albumId, CancellationToken cancellationToken = default);
    Task<int> RemoveForUser(long userId, CancellationToken cancellationToken = default);
    Task IncrementClicks(Guid id, CancellationToken cancellationToken = default);
    Task<List<AlbumShareLink>> ListPage(long ownerId, DateTime? cursorCreatedAt, Guid? cursorId, int limit, CancellationToken cancellationToken = default);
}
