using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests._Helpers;

/// <summary>
/// Реальный <see cref="IdentityContext"/> поверх SQLite для тестов хранилищ, использующих ExecuteUpdate/ExecuteDelete
/// (InMemory их не поддерживает). БД — временный файл, а не :memory:, чтобы каждый контекст имел собственное
/// соединение: тогда параллельные запросы действительно конкурируют, а SQLite сериализует записи.
/// </summary>
internal sealed class SqliteIdentityContext : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"identity-tests-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;

    public IdentityContext Context { get; }

    public SqliteIdentityContext()
    {
        _connectionString = $"DataSource={_path};Pooling=False";

        Context = CreateAdditionalContext();
        Context.Database.EnsureCreated();
    }

    /// <summary>Ещё один контекст со своим соединением — имитирует параллельный запрос с отдельным scope.</summary>
    public IdentityContext CreateAdditionalContext()
    {
        var options = new DbContextOptionsBuilder<IdentityContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new IdentityContext(options);
    }

    public void Dispose()
    {
        Context.Dispose();

        // EF включает WAL: рядом с файлом БД лежат -wal/-shm.
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(_path + suffix);
        }
    }
}
