using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace BarkCloud.Files.Tests._Helpers;

/// <summary>
/// Временная база PostgreSQL с применёнными миграциями Files — для проверок, которые SQLite
/// не подтверждает (advisory-блокировки, конкурентные транзакции). Строка подключения — в
/// <see cref="ConnectionStringVariable"/>; без неё тесты пропускаются (в CI — падают).
/// </summary>
public sealed class PostgresFilesDatabase : IAsyncDisposable
{
    public const string ConnectionStringVariable = "BARKCLOUD_TEST_POSTGRES";

    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private readonly NpgsqlDataSource _dataSource;

    private PostgresFilesDatabase(string adminConnectionString, string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = true,
            MaxPoolSize = 20,
        };
        _dataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);
    }

    public static string? SkipReason =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)) &&
        !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            ? $"Для PostgreSQL-тестов задайте {ConnectionStringVariable}."
            : null;

    public static async Task<PostgresFilesDatabase> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Не задана тестовая строка подключения {ConnectionStringVariable}.");

        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;
        var databaseName = $"barkcloud_files_f13_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var database = new PostgresFilesDatabase(adminConnectionString, databaseName);
        try
        {
            await using var context = database.CreateContext();
            await context.Database.MigrateAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public FilesContext CreateContext() => new(
        new DbContextOptionsBuilder<FilesContext>().UseNpgsql(_dataSource).Options);

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
