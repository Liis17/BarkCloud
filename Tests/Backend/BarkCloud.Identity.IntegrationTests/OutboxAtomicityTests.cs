using System.Security.Claims;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.SetPassword;
using BarkCloud.Identity.Features.ForceSetPasswordServer;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Shared.Identity;
using BarkCloud.Proto.Identity;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BarkCloud.Identity.IntegrationTests;

public class OutboxAtomicityTests
{
    [Fact]
    public async Task ForceSetPassword_WhenNotificationInsertFails_RollsBackPassword()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new PasswordsStorage(setup).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("old-password"));
            await RejectNotificationInsert(setup);
        }

        await using var context = database.CreateContext();
        var metrics = new MetricsCollector();
        var outbox = new NotificationOutbox(context, new ConfigurationBuilder().Build(), metrics,
            NullLogger<NotificationOutbox>.Instance);
        var handler = new ForceSetPasswordServerCommandHandler(new PasswordsStorage(context), outbox,
            NullLogger<ForceSetPasswordServerCommandHandler>.Instance, context, metrics);

        await FluentActions.Awaiting(() => handler.Handle(
                new ForceSetPasswordServerCommand { UserId = 42, NewPassword = "new-password" }, default))
            .Should().ThrowAsync<DbUpdateException>();

        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(observer).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmResetPassword_WhenNotificationInsertFails_RollsBackResetPasswordAndSessions()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var reset = await ResetPasswordTests.Seed(setup);
        await RejectNotificationInsert(setup);

        await using var context = database.CreateContext();
        await FluentActions.Awaiting(() => ResetPasswordTests.CreateHandler(context)
                .Handle(ResetPasswordTests.Command(reset.Id), default))
            .Should().ThrowAsync<DbUpdateException>();

        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await new ResetPasswordsStorage(observer).GetResetPassword(reset.Id))!.IsApproved.Should().BeFalse();
        (await new ResetPasswordsStorage(observer).GetResetPassword(reset.Id))!.OtpAttempts.Should().Be(1);
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(observer).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await new RefreshTokensStorage(observer, new JwtSettings()).GetRefreshTokens(42))
            .Select(t => t.Value).Should().BeEquivalentTo("old-current", "old-other");
        (await observer.RevokedSessions.AnyAsync()).Should().BeFalse();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetPassword_WhenEmailIsDisabled_CommitsPasswordWithoutNotification()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new PasswordsStorage(setup).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("old-password"));
            setup.AuthUserProperties.Add(new AuthUserProperty
            {
                UserId = 42,
                ReauthPasswordAttempts = 1,
                ReauthPasswordWindowEndsAt = DateTime.UtcNow.AddMinutes(15)
            });
            await setup.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var metrics = new MetricsCollector();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:EmailEnabled"] = "false" })
            .Build();
        var outbox = new NotificationOutbox(context, configuration, metrics, NullLogger<NotificationOutbox>.Instance);
        var handler = new SetPasswordCommandHandler(
            UserContextFor(42), new PasswordsStorage(context), new AuthPropertiesStorage(context),
            new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }),
            new PasswordChangedNotifier(outbox, new RequestContext()), metrics,
            NullLogger<SetPasswordCommandHandler>.Instance, context);

        await handler.Handle(new SetPasswordCommand { OldPassword = "old-password", NewPassword = "new-password" }, default);

        await using var observer = database.CreateContext();
        PasswordHasher.VerifyPassword("new-password", await new PasswordsStorage(observer).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetPassword_WhenNotificationInsertFails_RollsBackPasswordAndDoesNotReplayTrackedChanges()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new PasswordsStorage(setup).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("old-password"));
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_notification_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'notification insert failed'; END $$;
                CREATE TRIGGER reject_notification_insert BEFORE INSERT ON "PendingNotifications"
                FOR EACH ROW EXECUTE FUNCTION reject_notification_insert();
                """);
        }

        await using var context = database.CreateContext();
        var metrics = new MetricsCollector();
        var requestContext = new RequestContext { DeviceName = "Phone", OperationSystem = "Android", AppName = "BarkCloud", AppVersion = "1.0" };
        var outbox = new NotificationOutbox(context, new ConfigurationBuilder().Build(), metrics,
            NullLogger<NotificationOutbox>.Instance);
        var handler = new SetPasswordCommandHandler(
            UserContextFor(42),
            new PasswordsStorage(context),
            new AuthPropertiesStorage(context),
            new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }),
            new PasswordChangedNotifier(outbox, requestContext),
            metrics,
            NullLogger<SetPasswordCommandHandler>.Instance,
            context);

        await FluentActions.Awaiting(() => handler.Handle(
                new SetPasswordCommand { OldPassword = "old-password", NewPassword = "new-password" }, default))
            .Should().ThrowAsync<DbUpdateException>();

        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(observer).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetPassword_WhenDeferredNotificationTriggerRejectsCommit_RollsBackPasswordAndEvent()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new PasswordsStorage(setup).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("old-password"));
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_notification_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'notification commit rejected'; END $$;
                CREATE CONSTRAINT TRIGGER reject_notification_commit AFTER INSERT ON "PendingNotifications"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_notification_commit();
                """);
        }

        await using var context = database.CreateContext();
        var metrics = new MetricsCollector();
        var outbox = new NotificationOutbox(context, new ConfigurationBuilder().Build(), metrics,
            NullLogger<NotificationOutbox>.Instance);
        var handler = new SetPasswordCommandHandler(
            UserContextFor(42), new PasswordsStorage(context), new AuthPropertiesStorage(context),
            new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }),
            new PasswordChangedNotifier(outbox, new RequestContext()), metrics,
            NullLogger<SetPasswordCommandHandler>.Instance, context);

        await FluentActions.Awaiting(() => handler.Handle(
                new SetPasswordCommand { OldPassword = "old-password", NewPassword = "new-password" }, default))
            .Should().ThrowAsync<PostgresException>();

        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(observer).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetPassword_WhenCancellationArrivesBeforeNotificationWrite_RollsBackPassword()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new PasswordsStorage(setup).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("old-password"));
            setup.AuthUserProperties.Add(new AuthUserProperty
            {
                UserId = 42,
                ReauthPasswordAttempts = 1,
                ReauthPasswordWindowEndsAt = DateTime.UtcNow.AddMinutes(15)
            });
            await setup.SaveChangesAsync();
        }

        using var cancellation = new CancellationTokenSource();
        await using var context = database.CreateContext(new CancelAfterFirstSave(cancellation));
        var metrics = new MetricsCollector();
        var outbox = new NotificationOutbox(context, new ConfigurationBuilder().Build(), metrics,
            NullLogger<NotificationOutbox>.Instance);
        var handler = new SetPasswordCommandHandler(
            UserContextFor(42), new PasswordsStorage(context), new AuthPropertiesStorage(context),
            new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }),
            new PasswordChangedNotifier(outbox, new RequestContext()), metrics,
            NullLogger<SetPasswordCommandHandler>.Instance, context);

        await FluentActions.Awaiting(() => handler.Handle(
                new SetPasswordCommand { OldPassword = "old-password", NewPassword = "new-password" }, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        PasswordHasher.VerifyPassword("old-password", await new PasswordsStorage(observer).GetUserPasswordHash(42))
            .Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    private static UserContext UserContextFor(long userId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(IdentityClaims.UserId, userId.ToString()),
            new Claim(IdentityClaims.TokenType, TokenType.User.ToString()),
            new Claim(IdentityClaims.DeviceId, "device-1")
        ], authenticationType: "test");

        return new UserContext(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        });
    }

    private static Task RejectNotificationInsert(IdentityContext context)
        => context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_notification_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'notification insert failed'; END $$;
            CREATE TRIGGER reject_notification_insert BEFORE INSERT ON "PendingNotifications"
            FOR EACH ROW EXECUTE FUNCTION reject_notification_insert();
            """);

    private sealed class CancelAfterFirstSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private int _saved;

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saved) == 1)
            {
                cancellation.Cancel();
            }

            return ValueTask.FromResult(result);
        }
    }
}
