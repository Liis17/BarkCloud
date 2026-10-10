using System.Data.Common;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ConfirmAccount;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using Grpc.Core;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BarkCloud.Identity.IntegrationTests;

public class ConfirmAccountAtomicityTests
{
    [Fact]
    public async Task NotificationInsertFailure_RollsBackAndARepeatCanConfirmAfterLocalRollback()
    {
        var codeId = Guid.NewGuid();
        var users = CreateUsersClient();
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.ConfirmationCodes.Add(new ConfirmationCode
            {
                Id = codeId,
                OwnerId = 42,
                Type = ConfirmationCodeType.Registration,
                Value = "123456",
                Expires = DateTime.UtcNow.AddMinutes(5)
            });
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_notification_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'notification insert failed'; END $$;
                CREATE TRIGGER reject_notification_insert BEFORE INSERT ON "PendingNotifications"
                FOR EACH ROW EXECUTE FUNCTION reject_notification_insert();
                """);
        }

        await using var context = database.CreateContext();
        var handler = CreateHandler(context, users.Object);
        await FluentActions.Awaiting(() => handler.Handle(
                new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, default))
            .Should().ThrowAsync<DbUpdateException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await observer.ConfirmationCodes.AsNoTracking().AnyAsync(x => x.Id == codeId)).Should().BeTrue();
        (await observer.RefreshTokens.AsNoTracking().AnyAsync(x => x.UserId == 42)).Should().BeFalse();
        (await observer.PendingNotifications.AsNoTracking().AnyAsync()).Should().BeFalse();

        await observer.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_notification_insert ON \"PendingNotifications\"; DROP FUNCTION reject_notification_insert();");
        await using var retryContext = database.CreateContext();
        var response = await CreateHandler(retryContext, users.Object).Handle(
            new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, default);

        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        users.Verify(c => c.ConfirmUserAsync(It.Is<ConfirmUserRequest>(r => r.UserId == 42),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        await using var afterRetry = database.CreateContext();
        (await afterRetry.ConfirmationCodes.AsNoTracking().AnyAsync(x => x.Id == codeId)).Should().BeFalse();
        (await afterRetry.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == 42)).Should().Be(1);
        (await afterRetry.PendingNotifications.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentConfirmation_OnlyOneLocalSessionAndEventAreCreated()
    {
        var codeId = Guid.NewGuid();
        var response = new TaskCompletionSource<ConfirmUserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        users.Setup(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((ConfirmUserRequest _, Metadata _, DateTime? _, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref arrivals) == 2)
                    bothConfirmed.TrySetResult();
                return new AsyncUnaryCall<ConfirmUserResponse>(response.Task, Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess, () => new Metadata(), () => { });
            });

        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.ConfirmationCodes.Add(new ConfirmationCode
            {
                Id = codeId, OwnerId = 42, Type = ConfirmationCodeType.Registration,
                Value = "123456", Expires = DateTime.UtcNow.AddMinutes(5)
            });
            await setup.SaveChangesAsync();
        }

        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var first = ConfirmOnce(CreateHandler(firstContext, users.Object), codeId);
        var second = ConfirmOnce(CreateHandler(secondContext, users.Object), codeId);
        await bothConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        response.SetResult(new ConfirmUserResponse());
        var successes = await Task.WhenAll(first, second);

        successes.Count(x => x).Should().Be(1);
        await using var observer = database.CreateContext();
        (await observer.ConfirmationCodes.AsNoTracking().AnyAsync(x => x.Id == codeId)).Should().BeFalse();
        (await observer.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == 42)).Should().Be(1);
        (await observer.PendingNotifications.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task LastPermittedReservedAttemptCanStillConsumeRegistrationCode()
    {
        var codeId = Guid.NewGuid();
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.ConfirmationCodes.Add(new ConfirmationCode
            {
                Id = codeId, OwnerId = 42, Type = ConfirmationCodeType.Registration,
                Value = "123456", Expires = DateTime.UtcNow.AddMinutes(5),
                Attempts = AuthLimits.ChallengeMaxAttempts - 1
            });
            await setup.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        await CreateHandler(context, CreateUsersClient().Object).Handle(
            new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, default);

        await using var observer = database.CreateContext();
        (await observer.ConfirmationCodes.AsNoTracking().AnyAsync(x => x.Id == codeId)).Should().BeFalse();
        (await observer.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == 42)).Should().Be(1);
    }

    [Fact]
    public async Task RegistrationCodeReissuedBeforeLocalConsume_IsNotDeletedAndCreatesNoSession()
    {
        var codeId = Guid.NewGuid();
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.ConfirmationCodes.Add(new ConfirmationCode
            {
                Id = codeId, OwnerId = 42, Type = ConfirmationCodeType.Registration,
                Value = "123456", Expires = DateTime.UtcNow.AddMinutes(5)
            });
            await setup.SaveChangesAsync();
        }

        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        users.Setup(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                using var concurrent = database.CreateContext();
                concurrent.ConfirmationCodes.Where(x => x.Id == codeId)
                    .ExecuteUpdate(s => s.SetProperty(x => x.Value, "654321"));
            })
            .Returns(GrpcCallHelpers.AsyncUnary(new ConfirmUserResponse()));

        await using var context = database.CreateContext();
        await FluentActions.Awaiting(() => CreateHandler(context, users.Object).Handle(
                new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, default))
            .Should().ThrowAsync<BarkCloud.Shared.Exceptions.Identity.ConfirmationCodeIncorrectException>();

        await using var observer = database.CreateContext();
        var current = await observer.ConfirmationCodes.AsNoTracking().SingleAsync(x => x.Id == codeId);
        current.Value.Should().Be("654321");
        (await observer.RefreshTokens.AsNoTracking().AnyAsync(x => x.UserId == 42)).Should().BeFalse();
        (await observer.PendingNotifications.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DeferredCommitFailureAfterUsersConfirmation_RollsBackLocalChanges()
    {
        var codeId = Guid.NewGuid();
        var users = CreateUsersClient();
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.ConfirmationCodes.Add(new ConfirmationCode
            {
                Id = codeId, OwnerId = 42, Type = ConfirmationCodeType.Registration,
                Value = "123456", Expires = DateTime.UtcNow.AddMinutes(5)
            });
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_notification_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'notification commit rejected'; END $$;
                CREATE CONSTRAINT TRIGGER reject_notification_commit AFTER INSERT ON "PendingNotifications"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_notification_commit();
                """);
        }

        await using var context = database.CreateContext();
        await FluentActions.Awaiting(() => CreateHandler(context, users.Object).Handle(
                new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, default))
            .Should().ThrowAsync<PostgresException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        var currentCode = await observer.ConfirmationCodes.AsNoTracking().SingleAsync(x => x.Id == codeId);
        currentCode.Attempts.Should().Be(1);
        (await observer.RefreshTokens.AsNoTracking().AnyAsync(x => x.UserId == 42)).Should().BeFalse();
        (await observer.PendingNotifications.AsNoTracking().AnyAsync()).Should().BeFalse();
        users.Verify(c => c.ConfirmUserAsync(It.Is<ConfirmUserRequest>(r => r.UserId == 42),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellationAtCommitAfterUsersConfirmation_RollsBackLocalChanges()
    {
        var codeId = Guid.NewGuid();
        var users = CreateUsersClient();
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.ConfirmationCodes.Add(new ConfirmationCode
            {
                Id = codeId, OwnerId = 42, Type = ConfirmationCodeType.Registration,
                Value = "123456", Expires = DateTime.UtcNow.AddMinutes(5)
            });
            await setup.SaveChangesAsync();
        }

        using var cancellation = new CancellationTokenSource();
        await using var context = database.CreateContext(new CancelCommit(cancellation));
        await FluentActions.Awaiting(() => CreateHandler(context, users.Object).Handle(
                new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        var currentCode = await observer.ConfirmationCodes.AsNoTracking().SingleAsync(x => x.Id == codeId);
        currentCode.Attempts.Should().Be(1);
        (await observer.RefreshTokens.AsNoTracking().AnyAsync(x => x.UserId == 42)).Should().BeFalse();
        (await observer.PendingNotifications.AsNoTracking().AnyAsync()).Should().BeFalse();
        users.Verify(c => c.ConfirmUserAsync(It.Is<ConfirmUserRequest>(r => r.UserId == 42),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmailDisabled_ConsumesCodeAndCreatesSessionWithoutNotification()
    {
        var codeId = Guid.NewGuid();
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.ConfirmationCodes.Add(new ConfirmationCode
            {
                Id = codeId, OwnerId = 42, Type = ConfirmationCodeType.Registration,
                Value = "123456", Expires = DateTime.UtcNow.AddMinutes(5)
            });
            await setup.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:EmailEnabled"] = "false" })
            .Build();
        await using var context = database.CreateContext();
        var metrics = new MetricsCollector();
        await CreateHandler(context, CreateUsersClient().Object, configuration, metrics).Handle(
            new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, default);

        await using var observer = database.CreateContext();
        (await observer.ConfirmationCodes.AsNoTracking().AnyAsync(x => x.Id == codeId)).Should().BeFalse();
        (await observer.RefreshTokens.AsNoTracking().CountAsync(x => x.UserId == 42)).Should().Be(1);
        (await observer.PendingNotifications.AsNoTracking().AnyAsync()).Should().BeFalse();
        metrics.SnapshotAndReset().Should().NotContainKey("notification_outbox_enqueued");
    }

    private static async Task<bool> ConfirmOnce(ConfirmAccountCommandHandler handler, Guid codeId)
    {
        try
        {
            await handler.Handle(new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" }, default);
            return true;
        }
        catch (BarkCloud.Shared.Exceptions.Identity.ConfirmationCodeIncorrectException)
        {
            return false;
        }
    }

    private static Mock<UsersServerApi.UsersServerApiClient> CreateUsersClient()
    {
        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        users.Setup(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new ConfirmUserResponse()));
        return users;
    }

    private static ConfirmAccountCommandHandler CreateHandler(
        BarkCloud.Identity.Persistence.Contexts.IdentityContext context,
        UsersServerApi.UsersServerApiClient users, IConfiguration? configuration = null, MetricsCollector? metrics = null)
    {

        var requestContext = new RequestContext
        {
            DeviceName = "Pixel", DeviceId = "device-1", IpAddress = "203.0.113.5",
            OperationSystem = "Android", AppName = "BarkCloud", AppVersion = "1.0"
        };
        metrics ??= new MetricsCollector();
        var outbox = new NotificationOutbox(context, configuration ?? new ConfigurationBuilder().Build(), metrics,
            NullLogger<NotificationOutbox>.Instance);
        var policy = new Mock<IRegistrationPolicy>();
        policy.Setup(p => p.EnsureRegistrationEnabledAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var rateLimiter = new Mock<IAuthRateLimiter>();
        rateLimiter.Setup(r => r.EnsureSourceAsync(AuthLimits.ConfirmAccountByIp)).Returns(Task.CompletedTask);

        return new ConfirmAccountCommandHandler(new ConfirmationCodesStorage(context), users,
            new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }), context, requestContext,
            outbox, metrics, policy.Object, rateLimiter.Object, NullLogger<ConfirmAccountCommandHandler>.Instance);
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
