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

    public Task<List<TorrentEntity>> SearchByUser(long userId, string query, CancellationToken cancellationToken = default)
    {
        var pattern = LikeContainsPattern(query);
        return _context.Torrents.AsNoTracking()
            .Where(t => t.UserId == userId
                && (EF.Functions.ILike(t.Name, pattern, "\\")
                    || EF.Functions.ILike(t.InfoHash, pattern, "\\")
                    || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(t.Name, query) >= .45d))
            .OrderByDescending(t => t.AddedAt)
            .ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    private static string LikeContainsPattern(string query)
        => "%" + query.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    public Task<List<TorrentEntity>> ListAll() =>
        _context.Torrents.Include(t => t.Files).ToListAsync();

    public Task<TorrentEntity?> Get(Guid id, long userId) =>
        _context.Torrents.Include(t => t.Files)
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

    public Task<bool?> GetPaused(Guid id, long userId, CancellationToken ct = default) =>
        _context.Torrents.AsNoTracking()
            .Where(t => t.Id == id && t.UserId == userId)
            .Select(t => (bool?)t.Paused)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> SetPaused(Guid id, long userId, bool paused, CancellationToken ct = default) =>
        await _context.Torrents
            .Where(t => t.Id == id && t.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.Paused, paused), ct) > 0;

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
