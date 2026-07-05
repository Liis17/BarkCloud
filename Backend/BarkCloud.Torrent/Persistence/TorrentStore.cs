using BarkCloud.Torrent.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Torrent.Persistence;

public class TorrentStore : ITorrentStore
{
    private readonly TorrentContext _context;

    public TorrentStore(TorrentContext context) => _context = context;

    public Task<List<TorrentEntity>> ListByUser(long userId) =>
        _context.Torrents.Include(t => t.Files)
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.AddedAt)
            .ToListAsync();

    public Task<List<TorrentEntity>> ListAll() =>
        _context.Torrents.Include(t => t.Files).ToListAsync();

    public Task<TorrentEntity?> Get(Guid id, long userId) =>
        _context.Torrents.Include(t => t.Files)
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

    public Task<bool> ExistsByInfoHash(long userId, string infoHash) =>
        _context.Torrents.AnyAsync(t => t.UserId == userId && t.InfoHash == infoHash);

    public async Task Add(TorrentEntity entity)
    {
        await _context.Torrents.AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public Task Remove(TorrentEntity entity) =>
        _context.Torrents.Where(t => t.Id == entity.Id).ExecuteDeleteAsync();

    public Task SaveChanges() => _context.SaveChangesAsync();
}
