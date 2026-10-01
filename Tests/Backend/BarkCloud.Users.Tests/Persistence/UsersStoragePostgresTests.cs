using BarkCloud.Users.Domain;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BarkCloud.Users.Tests.Persistence;

[Trait("Category", "PostgreSQL")]
public class UsersStoragePostgresTests
{
    private const string PreviousMigration = "20260610090000_DefaultStorageLimitZero";

    [PostgresFact]
    public async Task CreateUser_EmptyDatabase_ReturnsGeneratedIdAndPersistsDraftWithContact()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);

        var user = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");

        user.Id.Should().Be(1);
        user.Contact.UserId.Should().Be(user.Id);
        user.RegistrationDate.Kind.Should().Be(DateTimeKind.Utc);
        user.RegistrationDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));

        await using var readContext = database.CreateContext();
        var savedUser = await new UsersStorage(readContext).GetById(user.Id);
        savedUser.Should().NotBeNull();
        savedUser!.Username.Should().Be("alice");
        savedUser.FirstName.Should().Be("Alice");
        savedUser.LastName.Should().Be("Smith");
        savedUser.IsDraft.Should().BeTrue();
        savedUser.Contact.Email.Should().Be("alice@example.test");
        savedUser.Contact.UserId.Should().Be(user.Id);
    }

    [PostgresFact]
    public async Task Migrate_LegacyUser_CreatesIdAboveLegacyMaximumAndPreservesRelatedData()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync(PreviousMigration);
        await using var context = database.CreateContext();
        var registrationDate = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var legacyUser = new User
        {
            Id = 4_000_000_000,
            Username = "legacy",
            FirstName = "Legacy",
            LastName = "User",
            RegistrationDate = registrationDate,
            Contact = new UserContact { Email = "legacy@example.test" },
            Privacy = new UserPrivacy { SearchableByUsername = false },
            IsDraft = false,
        };
        context.Users.Add(legacyUser);
        var device = new UserDevice
        {
            Id = Guid.NewGuid(),
            User = legacyUser,
            OriginalName = "Legacy device",
            AuthorizedAt = registrationDate,
        };
        context.UserDevices.Add(device);
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();
        var user = await new UsersStorage(context).CreateUser("bob", "Bob", "Smith", "bob@example.test");

        user.Id.Should().Be(4_000_000_001);
        user.Contact.UserId.Should().Be(user.Id);

        await using var readContext = database.CreateContext();
        var storage = new UsersStorage(readContext);
        var savedLegacyUser = await storage.GetById(4_000_000_000);
        savedLegacyUser.Should().NotBeNull();
        savedLegacyUser!.Username.Should().Be("legacy");
        savedLegacyUser.RegistrationDate.Should().Be(registrationDate);
        savedLegacyUser.IsDraft.Should().BeFalse();
        savedLegacyUser.Contact.Email.Should().Be("legacy@example.test");
        savedLegacyUser.Contact.UserId.Should().Be(4_000_000_000);
        (await storage.GetOrCreatePrivacy(4_000_000_000)).SearchableByUsername.Should().BeFalse();
        var savedDevice = await readContext.UserDevices.SingleAsync();
        savedDevice.Id.Should().Be(device.Id);
        savedDevice.UserId.Should().Be(4_000_000_000);
        savedDevice.OriginalName.Should().Be("Legacy device");
    }

    [PostgresTheory]
    [InlineData(false, 100L)]
    [InlineData(true, 101L)]
    public async Task Migrate_SequenceAheadOfUsers_PreservesNextId(bool isCalled, long expectedId)
    {
        await using var database = await PostgresUsersDatabase.CreateAsync(PreviousMigration);
        await using var context = database.CreateContext();
        context.Users.Add(new User
        {
            Id = 40,
            Username = "legacy",
            FirstName = "Legacy",
            LastName = "User",
            RegistrationDate = DateTime.UtcNow,
            Contact = new UserContact { Email = "legacy@example.test" },
        });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT setval(pg_get_serial_sequence('\"Users\"', 'Id')::regclass, 100, {isCalled})");

        await context.Database.MigrateAsync();
        var user = await new UsersStorage(context).CreateUser("alice", "Alice", "Smith", "alice@example.test");

        user.Id.Should().Be(expectedId);
    }

    [PostgresFact]
    public async Task Migrate_AllUsersDeleted_DoesNotReuseId()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync(PreviousMigration);
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        var firstUser = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        await storage.DeleteUser(firstUser.Id);

        await context.Database.MigrateAsync();
        var user = await storage.CreateUser("bob", "Bob", "Smith", "bob@example.test");

        firstUser.Id.Should().Be(1);
        user.Id.Should().Be(2);
    }

    [PostgresFact]
    public async Task Migrate_TransactionRolledBack_RestoresSequenceAndCanApplyAgain()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync(PreviousMigration);
        await using var context = database.CreateContext();
        context.Users.Add(new User
        {
            Id = 4_000_000_000,
            Username = "legacy",
            FirstName = "Legacy",
            LastName = "User",
            RegistrationDate = DateTime.UtcNow,
            Contact = new UserContact { Email = "legacy@example.test" },
        });
        await context.SaveChangesAsync();
        var script = context.GetService<IMigrator>().GenerateScript(
            PreviousMigration, "SyncUserIdSequence", MigrationsSqlGenerationOptions.NoTransactions);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await context.Database.ExecuteSqlRawAsync(script);
            await transaction.RollbackAsync();
        }

        var storage = new UsersStorage(context);
        var userBeforeMigration = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        userBeforeMigration.Id.Should().Be(1);

        await context.Database.MigrateAsync();
        var userAfterMigration = await storage.CreateUser("bob", "Bob", "Smith", "bob@example.test");
        userAfterMigration.Id.Should().Be(4_000_000_001);
    }

    [PostgresFact]
    public async Task CreateUser_ConcurrentRequests_Persist200UniqueUsersAndContacts()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        using var concurrencyLimit = new SemaphoreSlim(20);
        var ids = await Task.WhenAll(Enumerable.Range(0, 200).Select(async index =>
        {
            await concurrencyLimit.WaitAsync();
            try
            {
                await using var context = database.CreateContext();
                var username = $"user{index}";
                var user = await new UsersStorage(context).CreateUser(
                    username, "Concurrent", "User", $"{username}@example.test");
                user.Contact.UserId.Should().Be(user.Id);
                return user.Id;
            }
            finally
            {
                concurrencyLimit.Release();
            }
        }));

        ids.Should().HaveCount(200).And.OnlyHaveUniqueItems();
        await using var readContext = database.CreateContext();
        var savedUsers = await new UsersStorage(readContext).GetByIds(ids.ToList());
        savedUsers.Should().HaveCount(200);
        savedUsers.Should().AllSatisfy(user =>
        {
            user.IsDraft.Should().BeTrue();
            user.Contact.UserId.Should().Be(user.Id);
            user.Contact.Email.Should().Be($"{user.Username}@example.test");
        });
        (await readContext.Users.CountAsync()).Should().Be(200);
        (await readContext.UserContacts.CountAsync()).Should().Be(200);
    }

    [PostgresFact]
    public async Task Migrate_AppliedAgain_ContinuesSequence()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        var firstUser = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        var secondUser = await storage.CreateUser("bob", "Bob", "Smith", "bob@example.test");

        await context.Database.MigrateAsync();
        var thirdUser = await storage.CreateUser("carol", "Carol", "Smith", "carol@example.test");

        firstUser.Id.Should().Be(1);
        secondUser.Id.Should().Be(2);
        thirdUser.Id.Should().Be(3);
    }
}
