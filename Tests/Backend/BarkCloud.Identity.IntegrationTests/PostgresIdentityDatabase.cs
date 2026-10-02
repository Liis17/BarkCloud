using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Npgsql;

namespace BarkCloud.Identity.IntegrationTests;

public sealed class PostgresIdentityDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private PostgresIdentityDatabase(string adminConnectionString, string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;
    }

    public string ConnectionString { get; }

    public static async Task<PostgresIdentityDatabase> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("BARKCLOUD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Задайте BARKCLOUD_TEST_POSTGRES для интеграционных тестов Identity.");

        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var name = $"barkcloud_identity_f11_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var database = new PostgresIdentityDatabase(admin, name);
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

    public IdentityContext CreateContext(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<IdentityContext>().UseNpgsql(ConnectionString).AddInterceptors(interceptors).Options);

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
