using BarkCloud.Users.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace BarkCloud.Users.Tests._Helpers;

public sealed class PostgresUsersDatabase : IAsyncDisposable
{
    public const string ConnectionStringVariable = "BARKCLOUD_TEST_POSTGRES";

    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private readonly NpgsqlDataSource _dataSource;

    private PostgresUsersDatabase(string adminConnectionString, string databaseName)
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

    public static async Task<PostgresUsersDatabase> CreateAsync(string? targetMigration = null)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Не задана тестовая строка подключения {ConnectionStringVariable}.");

        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;
        var databaseName = $"barkcloud_users_f05_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var database = new PostgresUsersDatabase(adminConnectionString, databaseName);
        try
        {
            await using var context = database.CreateContext();
            if (targetMigration is null)
                await context.Database.MigrateAsync();
            else
                await context.GetService<IMigrator>().MigrateAsync(targetMigration);

            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public UsersContext CreateContext() => new(
        new DbContextOptionsBuilder<UsersContext>().UseNpgsql(_dataSource).Options);

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
