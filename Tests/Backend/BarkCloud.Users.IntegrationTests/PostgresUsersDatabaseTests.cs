using Npgsql;

namespace BarkCloud.Users.IntegrationTests;

public class PostgresUsersDatabaseTests
{
    [Fact]
    public async Task ConnectionString_AfterMigrations_CanOpenIndependentConnection()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_database()", connection);

        (await command.ExecuteScalarAsync()).Should().Be(connection.Database);
    }
}
