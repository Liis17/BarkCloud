using BarkCloud.Torrent.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Torrent.Persistence;

public class TorrentContext : DbContext
{
    public TorrentContext(DbContextOptions<TorrentContext> options) : base(options) { }

    public DbSet<TorrentEntity> Torrents { get; set; }

    public DbSet<TorrentFileEntity> TorrentFiles { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TorrentEntity>()
            .HasMany(t => t.Files)
            .WithOne(f => f.Torrent)
            .HasForeignKey(f => f.TorrentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TorrentEntity>()
            .HasIndex(t => t.UserId);

        base.OnModelCreating(modelBuilder);
    }
}
