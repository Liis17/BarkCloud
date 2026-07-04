using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BarkCloud.Torrent.Persistence;

public class TorrentContextFactory : IDesignTimeDbContextFactory<TorrentContext>
{
    public TorrentContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TorrentContext>();

        // Тестовая строка подключения — только для генерации миграций.
        optionsBuilder.UseNpgsql("Host=localhost;Database=barkcloud_torrent;Username=postgres;Password=password");

        return new TorrentContext(optionsBuilder.Options);
    }
}
