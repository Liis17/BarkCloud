using BarkCloud.Users.Domain;
using BarkCloud.Users.Persistence.Contexts;
using BarkCloud.Users.Persistence.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace BarkCloud.Users.IntegrationTests;

public class LoginMigrationTests
{
    private const string Migration = "20261001183000_EnforceUniqueUserLogins";
    private const string LegacyMigration = "20260610090000_DefaultStorageLimitZero";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migrate_DuplicateLogins_ReportsUserIdsPreservesDataAndCanRetry(bool duplicateEmail)
    {
        await using var database = await PostgresUsersDatabase.CreateAsync(LegacyMigration);
        await using var context = database.CreateContext();
        context.Users.Add(UserWithLogin(1001, "alice", "alice@example.test"));
        context.Users.Add(UserWithLogin(1002, duplicateEmail ? "bob" : "ALICE",
            duplicateEmail ? "ALICE@example.test" : "bob@example.test"));
        await context.SaveChangesAsync();

        var act = () => context.Database.MigrateAsync();

        var error = await act.Should().ThrowAsync<PostgresException>();
        error.Which.MessageText.Should().Contain("F04:").And.Contain("1001,1002");
        (await context.Database.GetAppliedMigrationsAsync()).Should().NotContain(Migration);
        (await IndexDefinition(context, "UX_Users_Username_Lower")).Should().BeNull();
        (await IndexDefinition(context, "IX_Users_Username_Lower")).Should().NotBeNull();
        var users = await new UsersStorage(context).GetByIds([1001, 1002]);
        users.Should().HaveCount(2);
        users.Single(u => u.Id == 1001).Contact.Email.Should().Be("alice@example.test");

        // Разрешение дубля выполняет оператор; здесь изменяются только тестовые данные.
        if (duplicateEmail)
            users.Single(u => u.Id == 1002).Contact.Email = "bob@example.test";
        else
            users.Single(u => u.Id == 1002).Username = "bob";
        await context.SaveChangesAsync();
        await context.Database.MigrateAsync();

        await AssertUniqueIndexes(context);
    }

    [Fact]
    public async Task Migrate_UpDownUp_RestoresLookupIndexAndCanEnforceUniquenessAgain()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var previousMigration = context.Database.GetMigrations().TakeWhile(m => m != Migration).Last();
        await AssertUniqueIndexes(context);

        await context.GetService<IMigrator>().MigrateAsync(previousMigration);

        (await IndexDefinition(context, "UX_Users_Username_Lower")).Should().BeNull();
        (await IndexDefinition(context, "UX_UserContacts_Email_Lower")).Should().BeNull();
        (await IndexDefinition(context, "IX_Users_Username_Lower")).Should().Contain("CREATE INDEX");
        context.Users.Add(UserWithLogin(1001, "alice", "alice@example.test"));
        context.Users.Add(UserWithLogin(1002, "ALICE", "bob@example.test"));
        await context.SaveChangesAsync();
        await new UsersStorage(context).DeleteUser(1002);

        await context.Database.MigrateAsync();

        await AssertUniqueIndexes(context);
        (await new UsersStorage(context).GetById(1001))!.Username.Should().Be("alice");
    }

    [Fact]
    public async Task Database_EmptyEmails_DoNotConflict()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);

        var alice = await storage.CreateUser("alice", "Alice", "Smith", "");
        var bob = await storage.CreateUser("bob", "Bob", "Smith", "");

        (await storage.GetByIds([alice.Id, bob.Id])).Should().HaveCount(2);
    }

    private static async Task AssertUniqueIndexes(UsersContext context)
    {
        (await IndexDefinition(context, "UX_Users_Username_Lower")).Should().Contain("CREATE UNIQUE INDEX");
        (await IndexDefinition(context, "UX_UserContacts_Email_Lower")).Should().Contain("CREATE UNIQUE INDEX").And.Contain("WHERE");
        (await IndexDefinition(context, "IX_Users_Username_Lower")).Should().BeNull();
        (await IndexDefinition(context, "IX_UserContacts_Email_Lower")).Should().Contain("CREATE INDEX");
    }

    private static Task<string?> IndexDefinition(UsersContext context, string index) =>
        context.Database.SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = {index}")
            .SingleOrDefaultAsync();

    private static User UserWithLogin(long id, string username, string email) => new()
    {
        Id = id, Username = username, FirstName = "Test", LastName = "User", RegistrationDate = DateTime.UtcNow,
        Contact = new UserContact { Email = email }, IsDraft = id == 1001,
    };
}
