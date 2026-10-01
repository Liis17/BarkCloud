using BarkCloud.Users.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace BarkCloud.Users.IntegrationTests;

public sealed class PostgresUsersDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private readonly NpgsqlDataSource _dataSource;

    private PostgresUsersDatabase(string adminConnectionString, string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        _dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = true,
            MaxPoolSize = 10,
        }.ConnectionString);
    }

    public static async Task<PostgresUsersDatabase> CreateAsync(string? targetMigration = null)
    {
        var connectionString = Environment.GetEnvironmentVariable("BARKCLOUD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Задайте BARKCLOUD_TEST_POSTGRES для интеграционных тестов Users.");

        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;
        var databaseName = $"barkcloud_users_f04_{Guid.NewGuid():N}";

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

    public UsersContext CreateContext(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<UsersContext>().UseNpgsql(_dataSource).AddInterceptors(interceptors).Options);

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
