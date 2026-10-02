using BarkCloud.Users.Persistence.Services;

using MassTransit.EntityFrameworkCoreIntegration;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BarkCloud.Users.IntegrationTests;

public class OutboxMigrationTests
{
    [Fact]
    public async Task OutboxMigration_DownAndUp_PreservesExistingUsers()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var user = await new UsersStorage(context).CreateUser("alice", "Alice", "Smith", "alice@example.test");
        var previous = context.Database.GetMigrations().Reverse().Skip(1).First();

        await context.GetService<IMigrator>().MigrateAsync(previous);
        await context.Database.MigrateAsync();

        await using var reader = database.CreateContext();
        (await new UsersStorage(reader).GetById(user.Id))!.Contact.Email.Should().Be("alice@example.test");
        (await reader.Set<OutboxMessage>().AnyAsync()).Should().BeFalse();
        reader.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
