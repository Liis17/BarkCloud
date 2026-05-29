using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IFavoriteFilesStorage
{
    Task<bool> Exists(long ownerId, Guid fileId, CancellationToken cancellationToken = default);
    Task Add(FavoriteFile item, CancellationToken cancellationToken = default);
    Task<int> Remove(long ownerId, Guid fileId, CancellationToken cancellationToken = default);
    Task<List<FavoriteFile>> ListPage(long ownerId, DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default);
}
