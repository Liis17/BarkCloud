using BarkCloud.Torrent.Domain;

namespace BarkCloud.Torrent.Persistence;

public interface ITorrentStore
{
    Task<List<TorrentEntity>> ListByUser(long userId);

    Task<List<TorrentEntity>> ListAll();

    Task<TorrentEntity?> Get(Guid id, long userId);

    Task<bool> ExistsByInfoHash(long userId, string infoHash);

    Task Add(TorrentEntity entity);

    Task Remove(TorrentEntity entity);

    Task SaveChanges();
}
