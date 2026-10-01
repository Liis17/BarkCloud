using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.AddDraftUser;
using BarkCloud.Users.Features.OverrideDraftUser;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace BarkCloud.Users.IntegrationTests;

public class UsersStorageTests
{
    [Fact]
    public async Task AddDraftUser_DraftCreatedBetweenChecks_ReusesDraftAtSameEmail()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        var barrier = new CommandBarrier(sql => sql.Contains("\"Email\")"), 1);
        await using var requestContext = database.CreateContext(barrier);
        var storage = new UsersStorage(requestContext);
        var handler = new AddDraftUserCommandHandler(storage,
            new ReservedUsernamesService(new ConfigurationBuilder().Build()), new MetricsCollector(),
            NullLogger<AddDraftUserCommandHandler>.Instance);

        var request = handler.Handle(new AddDraftUserCommand
        {
            Username = "alice", Email = "alice@example.test", FirstName = "New", LastName = "Name",
        }, default);
        await barrier.Reached;
        await using var concurrentContext = database.CreateContext();
        var alice = await new UsersStorage(concurrentContext).CreateUser("ALICE", "Alice", "Smith", "ALICE@example.test");
        barrier.Release();

        await FluentActions.Awaiting(() => request).Should().ThrowAsync<UserIsDraftException>();
        var retry = await new OverrideDraftUserCommandHandler(storage,
            NullLogger<OverrideDraftUserCommandHandler>.Instance).Handle(new OverrideDraftUserCommand
        {
            Username = "alice", Email = "alice@example.test", FirstName = "New", LastName = "Name",
        }, default);
        retry.UserId.Should().Be(alice.Id);
        var saved = (await storage.GetById(alice.Id))!;
        saved.FirstName.Should().Be("New");
        saved.Contact.Email.Should().Be("alice@example.test");
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("ALICE")]
    public async Task ChangeUsername_OwnName_AllowsAndPreservesRequestedCase(string username)
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        var alice = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");

        await storage.ChangeUsername(alice.Id, username);

        await using var readContext = database.CreateContext();
        var reader = new UsersStorage(readContext);
        (await reader.GetUserByUsername("aLiCe"))!.Id.Should().Be(alice.Id);
        (await reader.GetById(alice.Id))!.Username.Should().Be(username);
    }

    [Fact]
    public async Task ChangeUsername_ConcurrentRequests_OneSucceedsAndOtherReturnsUsernameExist()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var storage = new UsersStorage(setup);
        var alice = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        var bob = await storage.CreateUser("bob", "Bob", "Smith", "bob@example.test");
        var barrier = new CommandBarrier(sql => sql.Contains("\"Username\")"), 2);
        await using var firstContext = database.CreateContext(barrier);
        await using var secondContext = database.CreateContext(barrier);

        var first = Capture(() => new UsersStorage(firstContext).ChangeUsername(alice.Id, "shared"));
        var second = Capture(() => new UsersStorage(secondContext).ChangeUsername(bob.Id, "SHARED"));
        await barrier.Reached;
        barrier.Release();
        var errors = await Task.WhenAll(first, second);

        errors.Count(e => e is null).Should().Be(1);
        errors.Single(e => e is not null).Should().BeOfType<UsernameExistException>();
        await using var readContext = database.CreateContext();
        var reader = new UsersStorage(readContext);
        var owner = (await reader.GetUserByUsername("shared"))!;
        owner.Id.Should().BeOneOf(alice.Id, bob.Id);
        var loser = owner.Id == alice.Id ? bob : alice;
        (await reader.GetById(loser.Id))!.Username.Should().Be(loser.Username);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_ConcurrentInserts_OnlyOneDuplicateLoginIsPersisted(bool duplicateEmail)
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        var barrier = new CommandBarrier(sql => sql.Contains("INSERT INTO \"Users\""), 2, beforeExecution: true);
        await using var firstContext = database.CreateContext(barrier);
        await using var secondContext = database.CreateContext(barrier);
        firstContext.Users.Add(UserWithLogin(1001, "alice", "alice@example.test"));
        secondContext.Users.Add(UserWithLogin(1002,
            duplicateEmail ? "bob" : "ALICE", duplicateEmail ? "ALICE@example.test" : "bob@example.test"));

        var first = Capture(async () => { await firstContext.SaveChangesAsync(); });
        var second = Capture(async () => { await secondContext.SaveChangesAsync(); });
        await barrier.Reached;
        barrier.Release();
        var errors = await Task.WhenAll(first, second);

        errors.Count(e => e is null).Should().Be(1);
        var error = errors.Single(e => e is not null).Should().BeOfType<DbUpdateException>().Which;
        error.InnerException.Should().BeOfType<PostgresException>().Which.ConstraintName.Should().Be(
            duplicateEmail ? "UX_UserContacts_Email_Lower" : "UX_Users_Username_Lower");
        await using var readContext = database.CreateContext();
        (await new UsersStorage(readContext).GetByIds([1001, 1002])).Should().ContainSingle();
        (await readContext.UserContacts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateUser_PrimaryKeyConflict_IsNotReportedAsLoginConflict()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        await context.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('\"Users\"', 'Id')::regclass, 1, false);");

        var act = () => storage.CreateUser("bob", "Bob", "Smith", "bob@example.test");

        var error = await act.Should().ThrowAsync<DbUpdateException>();
        error.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("PK_Users");
    }

    [Fact]
    public async Task OverrideDraftUser_SameEmailWithDifferentCase_UpdatesDraftAndRetainsId()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        var alice = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        await storage.UpdateProfilePicture(alice.Id, "old.png", "preview.png");
        var handler = new OverrideDraftUserCommandHandler(storage,
            NullLogger<OverrideDraftUserCommandHandler>.Instance);

        var response = await handler.Handle(new OverrideDraftUserCommand
        {
            Username = " ALICE ", Email = " ALICE@example.test ", FirstName = " New ", LastName = " Name ",
        }, default);

        response.UserId.Should().Be(alice.Id);
        var saved = (await storage.GetById(alice.Id))!;
        saved.Username.Should().Be("ALICE");
        saved.FirstName.Should().Be("New");
        saved.LastName.Should().Be("Name");
        saved.Contact.Email.Should().Be("ALICE@example.test");
        saved.IsDraft.Should().BeTrue();
        saved.ProfilePicture.Should().BeNull();
    }

    [Fact]
    public async Task OverrideDraftUser_ContactUpdateConflict_RollsBackProfileAndEmail()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        var alice = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        await storage.CreateUser("bob", "Bob", "Smith", "bob@example.test");
        // Конфликт во второй записи транзакции: триггер подставляет занятый email.
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION conflict_contact_email() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                NEW."Email" := 'bob@example.test';
                RETURN NEW;
            END $$;
            CREATE TRIGGER conflict_contact_email BEFORE UPDATE ON "UserContacts"
            FOR EACH ROW EXECUTE FUNCTION conflict_contact_email();
            """);

        var act = () => storage.OverrideDraftUser(alice.Id, "newalice", "New", "Name", "ALICE@example.test");

        await act.Should().ThrowAsync<EmailExistException>();
        await using var readContext = database.CreateContext();
        var saved = (await new UsersStorage(readContext).GetById(alice.Id))!;
        saved.Username.Should().Be("alice");
        saved.FirstName.Should().Be("Alice");
        saved.Contact.Email.Should().Be("alice@example.test");
    }

    [Fact]
    public async Task OverrideDraftUser_ConfirmedAfterRead_RejectsAndPreservesProfile()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var setupContext = database.CreateContext();
        var alice = await new UsersStorage(setupContext).CreateUser("alice", "Alice", "Smith", "alice@example.test");
        var barrier = new CommandBarrier(sql => sql.Contains("\"Email\")"), 1);
        await using var overrideContext = database.CreateContext(barrier);
        var handler = new OverrideDraftUserCommandHandler(new UsersStorage(overrideContext),
            NullLogger<OverrideDraftUserCommandHandler>.Instance);

        var update = handler.Handle(new OverrideDraftUserCommand
        {
            Username = "newalice", Email = "alice@example.test", FirstName = "New", LastName = "Name",
        }, default);
        await barrier.Reached;
        await new UsersStorage(setupContext).ChangeDraftStatus(alice.Id, false);
        barrier.Release();

        await FluentActions.Awaiting(() => update).Should().ThrowAsync<EmailExistException>();
        await using var readContext = database.CreateContext();
        var saved = (await new UsersStorage(readContext).GetById(alice.Id))!;
        saved.IsDraft.Should().BeFalse();
        saved.Username.Should().Be("alice");
        saved.FirstName.Should().Be("Alice");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateUser_LoginConflict_ReturnsDomainErrorAndAllowsNextCreate(bool duplicateEmail)
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");

        var act = () => storage.CreateUser(
            duplicateEmail ? "bob" : "ALICE", "Bob", "Smith",
            duplicateEmail ? "ALICE@example.test" : "bob@example.test");

        if (duplicateEmail)
            await act.Should().ThrowAsync<EmailExistException>();
        else
            await act.Should().ThrowAsync<UsernameExistException>();

        var carol = await storage.CreateUser("carol", "Carol", "Smith", "carol@example.test");
        await using var readContext = database.CreateContext();
        var reader = new UsersStorage(readContext);
        (await reader.GetById(carol.Id))!.Username.Should().Be("carol");
        (await reader.GetUserByEmail("bob@example.test")).Should().BeNull();
        (await reader.GetUserByUsername("bob")).Should().BeNull();
    }

    [Theory]
    [InlineData(false, "UX_Users_Username_Lower")]
    [InlineData(true, "UX_UserContacts_Email_Lower")]
    public async Task Database_DuplicateLoginWithDifferentCase_RejectsDirectInsert(bool duplicateEmail, string index)
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        context.Users.Add(new User
        {
            Id = 100,
            Username = duplicateEmail ? "bob" : "ALICE",
            FirstName = "Bob",
            LastName = "Smith",
            RegistrationDate = DateTime.UtcNow,
            Contact = new UserContact { Email = duplicateEmail ? "ALICE@example.test" : "bob@example.test" },
            IsDraft = true,
        });

        var act = () => context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<DbUpdateException>();
        exception.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be(index);
        await using var readContext = database.CreateContext();
        (await new UsersStorage(readContext).GetUserByUsername("alice"))!.Contact.Email.Should().Be("alice@example.test");
        (await new UsersStorage(readContext).GetById(100)).Should().BeNull();
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("ALICE")]
    public async Task ChangeUsername_NameOwnedByAnotherUser_RejectsAndPreservesUser(string username)
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new UsersStorage(context);
        await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        var bob = await storage.CreateUser("bob", "Bob", "Smith", "bob@example.test");

        var act = () => storage.ChangeUsername(bob.Id, username);

        await act.Should().ThrowAsync<UsernameExistException>();
        await using var readContext = database.CreateContext();
        (await new UsersStorage(readContext).GetById(bob.Id))!.Username.Should().Be("bob");
    }

    private static User UserWithLogin(long id, string username, string email) => new()
    {
        Id = id, Username = username, FirstName = "Test", LastName = "User", RegistrationDate = DateTime.UtcNow,
        Contact = new UserContact { Email = email }, IsDraft = true,
    };

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
