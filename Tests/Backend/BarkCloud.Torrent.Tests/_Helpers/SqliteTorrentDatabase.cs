using BarkCloud.Torrent.Domain;
using BarkCloud.Torrent.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BarkCloud.Torrent.Tests._Helpers;

/// <summary>Файловая SQLite-БД для тестов persistence с независимыми контекстами.</summary>
internal sealed class SqliteTorrentDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"torrent-tests-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly ServiceProvider _provider;

    public IServiceScopeFactory ScopeFactory { get; }

    public SqliteTorrentDatabase(params IInterceptor[] interceptors)
    {
        _connectionString = $"DataSource={_path};Pooling=False";

        using (var context = CreateAdditionalContext())
            context.Database.EnsureCreated();

        _provider = new ServiceCollection()
            .AddDbContext<TorrentContext>(options =>
            {
                options.UseSqlite(_connectionString);
                options.AddInterceptors(interceptors);
            })
            .BuildServiceProvider();
        ScopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    public TorrentContext CreateAdditionalContext()
    {
        var options = new DbContextOptionsBuilder<TorrentContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new TorrentContext(options);
    }

    public async Task SeedAsync(params TorrentEntity[] entities)
    {
        await using var context = CreateAdditionalContext();
        context.Torrents.AddRange(entities);
        await context.SaveChangesAsync();
    }

    public async Task<TorrentEntity?> ReadAsync(Guid id)
    {
        await using var context = CreateAdditionalContext();
        return await context.Torrents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var context = CreateAdditionalContext();
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    public void Dispose()
    {
        _provider.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            File.Delete(_path + suffix);
    }
}
