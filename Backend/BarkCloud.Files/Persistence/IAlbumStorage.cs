using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IAlbumStorage
{
    Task<Album?> GetAlbum(Guid id, CancellationToken cancellationToken = default);
    Task<bool> AlbumNameExists(long ownerId, string name, CancellationToken cancellationToken = default);
    Task<Album> AddAlbum(Album album, CancellationToken cancellationToken = default);
    Task UpdateAlbum(Album album, CancellationToken cancellationToken = default);
    Task RemoveAlbum(Album album, CancellationToken cancellationToken = default);
    Task<List<Album>> ListAlbumsPage(long ownerId, DateTime? cursorUpdatedAt, Guid? cursorAlbumId, int limit, CancellationToken cancellationToken = default);
    Task<Dictionary<Guid, int>> GetItemCounts(IEnumerable<Guid> albumIds, CancellationToken cancellationToken = default);
    Task<HashSet<Guid>> GetExistingItemFileIds(Guid albumId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default);
    Task AddItems(IEnumerable<AlbumItem> items, CancellationToken cancellationToken = default);
    Task<int> RemoveItems(Guid albumId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default);
    Task<int> RemoveFileFromAllAlbums(long ownerId, Guid fileId, CancellationToken cancellationToken = default);
    Task<List<AlbumItem>> ListItemsPage(Guid albumId, DateTime? cursorAddedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default);
    Task<AlbumItem?> GetFirstItem(Guid albumId, CancellationToken cancellationToken = default);
}
