using System.Security.Claims;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ConfirmOtpVerification;
using BarkCloud.Identity.Features.DisableOtpVerification;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using OtpNet;

using OtpType = BarkCloud.Identity.Domain.OtpType;
using Npgsql;

namespace BarkCloud.Identity.IntegrationTests;

public class TwoFactorAtomicityTests
{
    [Fact]
    public async Task ConfirmAuthenticator_WhenNotificationInsertFails_RestoresPendingSecret()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var key = KeyGeneration.GenerateRandomKey(20);
        var pendingSecret = Base32Encoding.ToString(key);
        await Seed(database, new AuthUserProperty
        {
            UserId = 42, SelectedOtpType = OtpType.Authenticator, OtpEnabled = true,
            OtpSecret = "old-secret", PendingOtpSecret = pendingSecret,
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5)
        }, rejectNotifications: true);

        await using var context = database.CreateContext();
        var handler = CreateConfirmHandler(context, new MetricsCollector());
        await FluentActions.Awaiting(() => handler.Handle(
                new ConfirmOtpVerificationCommand { OtpCode = new Totp(key).ComputeTotp() }, default))
            .Should().ThrowAsync<DbUpdateException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        var properties = await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42);
        properties.OtpSecret.Should().Be("old-secret");
        properties.PendingOtpSecret.Should().Be(pendingSecret);
        properties.OtpEnabled.Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmEmail_WhenNotificationInsertFails_RestoresCodeAndDisabledState()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await Seed(database, new AuthUserProperty
        {
            UserId = 42, SelectedOtpType = OtpType.Email, EmailOtpEnabled = false,
            LastEmailAuthCode = "123456", EmailAuthCodePurpose = EmailAuthCodePurpose.EnableEmailOtp,
            EmailAuthCodeIssuedAt = DateTime.UtcNow, EmailAuthCodeExpiresAt = DateTime.UtcNow.AddMinutes(5)
        }, rejectNotifications: true);

        await using var context = database.CreateContext();
        var handler = CreateConfirmHandler(context, new MetricsCollector());
        await FluentActions.Awaiting(() => handler.Handle(
                new ConfirmOtpVerificationCommand { OtpCode = "123456" }, default))
            .Should().ThrowAsync<DbUpdateException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        var properties = await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42);
        properties.EmailOtpEnabled.Should().BeFalse();
        properties.LastEmailAuthCode.Should().Be("123456");
        properties.EmailAuthCodeAttempts.Should().Be(1);
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DisableAuthenticator_WhenNotificationInsertFails_LeavesOtpEnabled()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var key = KeyGeneration.GenerateRandomKey(20);
        await Seed(database, new AuthUserProperty
        {
            UserId = 42, OtpEnabled = true, OtpSecret = Base32Encoding.ToString(key)
        }, rejectNotifications: true);

        await using var context = database.CreateContext();
        var handler = CreateDisableHandler(context, new MetricsCollector());
        await FluentActions.Awaiting(() => handler.Handle(new DisableOtpVerificationCommand
                { OptType = OtpTypeId.Authenticator, OtpCode = new Totp(key).ComputeTotp() }, default))
            .Should().ThrowAsync<DbUpdateException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42)).OtpEnabled.Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DisableEmail_WhenNotificationInsertFails_LeavesEmailOtpEnabled()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await Seed(database, new AuthUserProperty { UserId = 42, EmailOtpEnabled = true });
        await SeedPassword(database);
        await RejectNotificationInsert(database);

        await using var context = database.CreateContext();
        var handler = CreateDisableHandler(context, new MetricsCollector());
        await FluentActions.Awaiting(() => handler.Handle(new DisableOtpVerificationCommand
                { OptType = OtpTypeId.Email, Password = "password" }, default))
            .Should().ThrowAsync<DbUpdateException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42)).EmailOtpEnabled.Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmEmail_Success_CommitsCodeEnablementAndEvent()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await Seed(database, new AuthUserProperty
        {
            UserId = 42, SelectedOtpType = OtpType.Email, EmailOtpEnabled = false,
            LastEmailAuthCode = "123456", EmailAuthCodePurpose = EmailAuthCodePurpose.EnableEmailOtp,
            EmailAuthCodeIssuedAt = DateTime.UtcNow, EmailAuthCodeExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        await using var context = database.CreateContext();
        await CreateConfirmHandler(context, new MetricsCollector()).Handle(
            new ConfirmOtpVerificationCommand { OtpCode = "123456" }, default);

        await using var observer = database.CreateContext();
        var properties = await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42);
        properties.EmailOtpEnabled.Should().BeTrue();
        properties.LastEmailAuthCode.Should().BeNull();
        (await observer.PendingNotifications.Select(x => x.Type).ToListAsync())
            .Should().Equal(NotificationType.TwoFactorMethodChanged);
    }

    [Fact]
    public async Task DisableAuthenticator_EmailDisabled_ChangesStateWithoutOutboxRow()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var key = KeyGeneration.GenerateRandomKey(20);
        await Seed(database, new AuthUserProperty
        {
            UserId = 42, OtpEnabled = true, OtpSecret = Base32Encoding.ToString(key)
        });

        await using var context = database.CreateContext();
        await CreateDisableHandler(context, new MetricsCollector(), emailEnabled: false).Handle(
            new DisableOtpVerificationCommand { OptType = OtpTypeId.Authenticator, OtpCode = new Totp(key).ComputeTotp() }, default);

        await using var observer = database.CreateContext();
        (await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42)).OtpEnabled.Should().BeFalse();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DisableEmail_WhenDeferredTriggerRejectsCommit_RestoresEnabledState()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await Seed(database, new AuthUserProperty { UserId = 42, EmailOtpEnabled = true });
        await SeedPassword(database);
        await using (var setup = database.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_notification_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'notification commit rejected'; END $$;
                CREATE CONSTRAINT TRIGGER reject_notification_commit AFTER INSERT ON "PendingNotifications"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_notification_commit();
                """);
        }

        await using var context = database.CreateContext();
        await FluentActions.Awaiting(() => CreateDisableHandler(context, new MetricsCollector()).Handle(
                new DisableOtpVerificationCommand { OptType = OtpTypeId.Email, Password = "password" }, default))
            .Should().ThrowAsync<PostgresException>();

        await using var observer = database.CreateContext();
        (await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42)).EmailOtpEnabled.Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DisableEmail_WhenCancelledBeforeNotificationWrite_RestoresEnabledState()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await Seed(database, new AuthUserProperty { UserId = 42, EmailOtpEnabled = true });
        await SeedPassword(database);
        using var cancellation = new CancellationTokenSource();
        await using var context = database.CreateContext(new CancelAfterSaveChanges(cancellation));

        await FluentActions.Awaiting(() => CreateDisableHandler(context, new MetricsCollector()).Handle(
                new DisableOtpVerificationCommand { OptType = OtpTypeId.Email, Password = "password" }, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42)).EmailOtpEnabled.Should().BeTrue();
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    private static async Task Seed(PostgresIdentityDatabase database, AuthUserProperty properties, bool rejectNotifications = false)
    {
        await using var context = database.CreateContext();
        context.AuthUserProperties.Add(properties);
        await context.SaveChangesAsync();
        if (rejectNotifications)
        {
            await CreateNotificationInsertTrigger(context);
        }
    }

    private static async Task SeedPassword(PostgresIdentityDatabase database)
    {
        await using var context = database.CreateContext();
        await new PasswordsStorage(context).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("password"));
    }

    private static async Task RejectNotificationInsert(PostgresIdentityDatabase database)
    {
        await using var context = database.CreateContext();
        await CreateNotificationInsertTrigger(context);
    }

    private static Task CreateNotificationInsertTrigger(IdentityContext context)
        => context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_notification_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'notification insert failed'; END $$;
            CREATE TRIGGER reject_notification_insert BEFORE INSERT ON "PendingNotifications"
            FOR EACH ROW EXECUTE FUNCTION reject_notification_insert();
            """);

    private static ConfirmOtpVerificationCommandHandler CreateConfirmHandler(IdentityContext context, MetricsCollector metrics)
    {
        var requestContext = RequestContext();
        return new ConfirmOtpVerificationCommandHandler(UserContext(), context, new AuthPropertiesStorage(context),
            CreateOutbox(context, metrics), requestContext, metrics,
            new AuthRateLimiter(new AttemptCountersStorage(context), requestContext,
                NullLogger<AuthRateLimiter>.Instance), NullLogger<ConfirmOtpVerificationCommandHandler>.Instance);
    }

    private static DisableOtpVerificationCommandHandler CreateDisableHandler(IdentityContext context, MetricsCollector metrics,
        bool emailEnabled = true)
    {
        var requestContext = RequestContext();
        var properties = new AuthPropertiesStorage(context);
        var verifier = new ReauthPasswordVerifier(properties, new PasswordsStorage(context));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:EmailEnabled"] = emailEnabled.ToString()
        }).Build();
        return new DisableOtpVerificationCommandHandler(UserContext(), context, properties, verifier,
            new NotificationOutbox(context, configuration, metrics, NullLogger<NotificationOutbox>.Instance),
            requestContext, metrics, new AuthRateLimiter(new AttemptCountersStorage(context), requestContext,
                NullLogger<AuthRateLimiter>.Instance), NullLogger<DisableOtpVerificationCommandHandler>.Instance);
    }

    private static NotificationOutbox CreateOutbox(IdentityContext context, MetricsCollector metrics)
        => new(context, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:EmailEnabled"] = "true"
        }).Build(), metrics, NullLogger<NotificationOutbox>.Instance);

    private static UserContext UserContext()
    {
        var identity = new ClaimsIdentity([new Claim(IdentityClaims.UserId, "42")], "integration-test");
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return new UserContext(accessor);
    }

    private static RequestContext RequestContext() => new()
    {
        DeviceName = "Phone", OperationSystem = "Android", AppName = "BarkCloud", AppVersion = "1.0",
        DeviceId = "device-1", IpAddress = "1.1.1.1"
    };

    private sealed class CancelAfterSaveChanges(CancellationTokenSource cancellation) : SaveChangesInterceptor
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
