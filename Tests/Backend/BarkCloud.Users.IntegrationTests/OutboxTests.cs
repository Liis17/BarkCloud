using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Queue.Users;
using BarkCloud.Users.Features.DeleteAccount;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Contexts;
using BarkCloud.Users.Persistence.Services;

using MassTransit.EntityFrameworkCoreIntegration;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using System.Data.Common;

namespace BarkCloud.Users.IntegrationTests;

public class OutboxTests
{
    [Fact]
    public async Task DeleteAccount_RabbitUnavailable_DeliversWhenConnectionRecovers()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var observer = new RabbitObserver();
        await observer.StartAsync();
        await using var proxy = new RabbitConnectionProxy(OutboxTestHost.RabbitAddress);
        await using var producer = new OutboxTestHost(database, address: proxy.Address);
        await producer.StartAsync();
        using var scope = producer.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IUsersStorage>()
            .CreateUser("alice", "Alice", "Smith", "alice@example.test");

        await DeleteAsync(scope.ServiceProvider, user.Id).WaitAsync(TimeSpan.FromSeconds(5));

        await using var reader = database.CreateContext();
        (await new UsersStorage(reader).GetById(user.Id)).Should().BeNull();
        (await reader.Set<OutboxMessage>().CountAsync()).Should().Be(1);
        proxy.Enable();
        (await observer.ReceiveAsync(1)).Should().ContainSingle().Which
            .Should().BeOfType<UserDeleted>().Which.UserId.Should().Be(user.Id);
        await AssertDrained(database);
    }

    [Fact]
    public async Task DeleteAccount_OutboxInsertFails_RollsBackUserAndRelatedData()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var producer = new OutboxTestHost(database, deliver: false);
        await producer.StartAsync();
        using var scope = producer.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UsersContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IUsersStorage>();
        var user = await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test");
        await storage.GetOrCreatePrivacy(user.Id);
        await new DevicesStorage(context).RegisterOrUpdateDevice(Guid.NewGuid(), user.Id, "Phone", null, null, null);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_outbox() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'outbox write failed'; END $$;
            CREATE TRIGGER reject_outbox BEFORE INSERT ON "OutboxMessage"
            FOR EACH ROW EXECUTE FUNCTION reject_outbox();
            """);

        await FluentActions.Awaiting(() => DeleteAsync(scope.ServiceProvider, user.Id))
            .Should().ThrowAsync<DbUpdateException>();

        await using var reader = database.CreateContext();
        (await new UsersStorage(reader).GetById(user.Id))!.Contact.Email.Should().Be("alice@example.test");
        (await new DevicesStorage(reader).GetDevicesByUserId(user.Id)).Should().ContainSingle();
        (await reader.UserPrivacies.CountAsync()).Should().Be(1);
        (await reader.Set<OutboxMessage>().AnyAsync()).Should().BeFalse();
        producer.Services.GetRequiredService<MetricsCollector>().SnapshotAndReset()
            .Should().NotContainKey("accounts_deleted").And.NotContainKey("user_deleted_published");
    }

    [Fact]
    public async Task DeleteAccount_CommitCanceled_RollsBackUserAndOutbox()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var user = await new UsersStorage(setup).CreateUser("alice", "Alice", "Smith", "alice@example.test");
        using var cancellation = new CancellationTokenSource();
        await using var producer = new OutboxTestHost(database, false, null, new CancelCommit(cancellation));
        await producer.StartAsync();
        using var scope = producer.Services.CreateScope();

        await FluentActions.Awaiting(() => DeleteAsync(scope.ServiceProvider, user.Id, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        await using var reader = database.CreateContext();
        (await new UsersStorage(reader).GetById(user.Id)).Should().NotBeNull();
        (await reader.Set<OutboxMessage>().AnyAsync()).Should().BeFalse();
        producer.Services.GetRequiredService<MetricsCollector>().SnapshotAndReset().Should().NotContainKey("accounts_deleted");
    }

    [Fact]
    public async Task ProfileEvents_AllPublicationsInSameScope_AreSavedAndDelivered()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var observer = new RabbitObserver();
        await observer.StartAsync();
        await using var producer = new OutboxTestHost(database);
        await producer.StartAsync();
        using var scope = producer.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<UserInfoQueueSender>();

        await sender.NameChangedEvent(42, "Alice", "Smith");
        await sender.UsernameChangedEvent(42, "alice");
        await sender.UserChangedAvatarEvent(42, "avatar.png", "preview.png");
        await sender.BioChangedEvent(42, "Hello");

        var messages = await observer.ReceiveAsync(4);
        messages.OfType<UserChangedName>().Single().NewFirstName.Should().Be("Alice");
        messages.OfType<UserChangedUsername>().Single().NewUsername.Should().Be("alice");
        messages.OfType<UserChangedAvatar>().Single().ProfilePictureUrl.Should().Be("avatar.png");
        messages.OfType<UserChangedBio>().Single().NewBio.Should().Be("Hello");
        await AssertDrained(database);
    }

    [Fact]
    public async Task DeleteAccount_ProcessRestartsAfterCommit_DeliversSavedEvent()
    {
        await using var database = await PostgresUsersDatabase.CreateAsync();
        await using var observer = new RabbitObserver();
        await observer.StartAsync();
        long userId;
        await using (var first = new OutboxTestHost(database, deliver: false))
        {
            await first.StartAsync();
            using var scope = first.Services.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IUsersStorage>();
            userId = (await storage.CreateUser("alice", "Alice", "Smith", "alice@example.test")).Id;
            await DeleteAsync(scope.ServiceProvider, userId);
            await using var reader = database.CreateContext();
            (await new UsersStorage(reader).GetById(userId)).Should().BeNull();
            (await reader.Set<OutboxMessage>().CountAsync()).Should().Be(1);
        }

        await using var restarted = new OutboxTestHost(database);
        await restarted.StartAsync();
        (await observer.ReceiveAsync(1)).Should().ContainSingle().Which
            .Should().BeOfType<UserDeleted>().Which.UserId.Should().Be(userId);
        await AssertDrained(database);
    }

    internal static Task DeleteAsync(IServiceProvider services, long userId, CancellationToken cancellationToken = default)
    {
        var context = services.GetRequiredService<UsersContext>();
        return new DeleteAccountCommandHandler(services.GetRequiredService<IUsersStorage>(),
            DeleteAccountTests.CreateUserContext(userId), services.GetRequiredService<UserInfoQueueSender>(),
            services.GetRequiredService<MetricsCollector>(), NullLogger<DeleteAccountCommandHandler>.Instance, context)
            .Handle(new DeleteAccountCommand(), cancellationToken);
    }

    internal static async Task AssertDrained(PostgresUsersDatabase database)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = database.CreateContext();
        while (await reader.Set<OutboxMessage>().AnyAsync(timeout.Token))
            await Task.Delay(50, timeout.Token);
    }

    private sealed class CancelCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
