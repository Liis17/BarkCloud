using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
    public NpgsqlDataSource DataSource { get; }

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
        DataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);
    }

    public static string? SkipReason =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)) &&
        !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            ? $"Для PostgreSQL-тестов задайте {ConnectionStringVariable}."
            : null;

    public static async Task<PostgresFilesDatabase> CreateAsync(string? targetMigration = null)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Не задана тестовая строка подключения {ConnectionStringVariable}.");

        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;
        var databaseName = $"barkcloud_files_test_{Guid.NewGuid():N}";

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
            await context.GetService<IMigrator>().MigrateAsync(targetMigration);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public FilesContext CreateContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<FilesContext>().UseNpgsql(DataSource);
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        return new FilesContext(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
