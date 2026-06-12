using BarkCloud.Files.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Tests._Helpers;

/// <summary>
/// Реальный <see cref="FilesContext"/> поверх SQLite in-memory для интеграционных тестов
/// сервисов, работающих с БД напрямую (ExecuteDelete, primitive collections, корреляционные
/// подзапросы). Соединение держится открытым на всё время жизни — БД живёт, пока открыт connection.
/// </summary>
internal sealed class SqliteFilesContext : IDisposable
{
    private readonly SqliteConnection _connection;

    public FilesContext Context { get; }

    public SqliteFilesContext()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<FilesContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new FilesContext(options);
        Context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
