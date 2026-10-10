using System.Text.Json;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using Grpc.Core;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BarkCloud.Identity.IntegrationTests;

public class SessionAtomicityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssueSession_WhenNotificationInsertFails_PreservesExistingRefresh(bool explicitDevice)
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new RefreshTokensStorage(setup, new JwtSettings { ExpiryMinutes = 60 })
                .CreateNewRefreshToken("old-refresh", 42, DeviceId(explicitDevice), 1);
            await RejectNotificationInsert(setup);
        }

        await using var context = database.CreateContext();
        var issuer = CreateIssuer(context);
        var act = () => explicitDevice
            ? issuer.IssueAsync(42, new SessionDevice(DeviceId(true), "Server", "Linux", "BarkCloud.Web", "1.1.1.1"), default)
            : issuer.IssueAsync(42, default);

        await FluentActions.Awaiting(act).Should().ThrowAsync<DbUpdateException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await new RefreshTokensStorage(observer, new JwtSettings { ExpiryMinutes = 60 }).GetRefreshTokens(42))
            .Select(x => x.Value).Should().Equal("old-refresh");
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssueSession_Success_CommitsRefreshAndLoginEvent(bool explicitDevice)
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new RefreshTokensStorage(setup, new JwtSettings { ExpiryMinutes = 60 })
                .CreateNewRefreshToken("old-refresh", 42, DeviceId(explicitDevice), 1);
        }

        await using var context = database.CreateContext();
        var issuer = CreateIssuer(context);
        var response = explicitDevice
            ? await issuer.IssueAsync(42, new SessionDevice(DeviceId(true), "Server", "Linux", "BarkCloud.Web", "1.1.1.1"), default)
            : await issuer.IssueAsync(42, default);

        await using var observer = database.CreateContext();
        var tokens = await new RefreshTokensStorage(observer, new JwtSettings { ExpiryMinutes = 60 }).GetRefreshTokens(42);
        tokens.Should().ContainSingle(x => x.Value == response.RefreshToken.Value && x.DeviceId == DeviceId(explicitDevice));
        var notification = await observer.PendingNotifications.SingleAsync();
        notification.Type.Should().Be(NotificationType.SuccessfulLogin);
        JsonSerializer.Deserialize<Dictionary<string, string>>(notification.PayloadJson)! ["location"]
            .Should().Be("Example, Region, City");
    }

    [Fact]
    public async Task IssueSession_WhenDeferredTriggerRejectsCommit_RestoresOldRefresh()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new RefreshTokensStorage(setup, new JwtSettings { ExpiryMinutes = 60 })
                .CreateNewRefreshToken("old-refresh", 42, "device-1", 1);
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_notification_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'notification commit rejected'; END $$;
                CREATE CONSTRAINT TRIGGER reject_notification_commit AFTER INSERT ON "PendingNotifications"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_notification_commit();
                """);
        }

        await using var context = database.CreateContext();
        await FluentActions.Awaiting(() => CreateIssuer(context).IssueAsync(42, default))
            .Should().ThrowAsync<PostgresException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await new RefreshTokensStorage(observer, new JwtSettings { ExpiryMinutes = 60 }).GetRefreshTokens(42))
            .Select(x => x.Value).Should().Equal("old-refresh");
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IssueSession_WhenCancelledBeforeNotificationWrite_RollsBackReplacementAndDoesNotReplay()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new RefreshTokensStorage(setup, new JwtSettings { ExpiryMinutes = 60 })
                .CreateNewRefreshToken("old-refresh", 42, "device-1", 1);
        }

        using var cancellation = new CancellationTokenSource();
        await using var context = database.CreateContext(new CancelAfterSaveChanges(2, cancellation));
        await FluentActions.Awaiting(() => CreateIssuer(context).IssueAsync(42, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        (await new RefreshTokensStorage(observer, new JwtSettings { ExpiryMinutes = 60 }).GetRefreshTokens(42))
            .Select(x => x.Value).Should().Equal("old-refresh");
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    private static string DeviceId(bool explicitDevice) => explicitDevice ? "server-device" : "device-1";

    private static SessionIssuer CreateIssuer(IdentityContext context)
    {
        var metrics = new MetricsCollector();
        var jwtSettings = new JwtSettings
        {
            SecretKey = "supersecretkey_at_least_32_chars_long_for_hs256!!",
            Issuer = "bark", Audience = "bark", ExpiryMinutes = 60
        };
        var refreshTokens = new RefreshTokensStorage(context, jwtSettings);
        var jwt = new JwtService(jwtSettings);
        var createToken = new CreateTokenCommandHandler(refreshTokens, jwt, metrics,
            NullLogger<CreateTokenCommandHandler>.Instance);
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .Returns((CreateTokenCommand command, CancellationToken cancellationToken) =>
                createToken.Handle(command, cancellationToken));

        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        users.Setup(c => c.RegisterDeviceAsync(It.IsAny<RegisterDeviceRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new RegisterDeviceResponse()));

        var location = new Mock<LocationClient>(new HttpClient(), metrics, NullLogger<LocationClient>.Instance);
        location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync(new IpLocation
        {
            Country = "Example", RegionName = "Region", City = "City"
        });

        return new SessionIssuer(users.Object, mediator.Object,
            new NotificationOutbox(context, new ConfigurationBuilder().Build(), metrics,
                NullLogger<NotificationOutbox>.Instance),
            refreshTokens, context,
            new RequestContext
            {
                DeviceId = "device-1", DeviceName = "Phone", OperationSystem = "Android",
                AppName = "BarkCloud", AppVersion = "1.0", IpAddress = "1.1.1.1"
            },
            location.Object, metrics, NullLogger<SessionIssuer>.Instance);
    }

    private static Task RejectNotificationInsert(IdentityContext context)
        => context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_notification_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'notification insert failed'; END $$;
            CREATE TRIGGER reject_notification_insert BEFORE INSERT ON "PendingNotifications"
            FOR EACH ROW EXECUTE FUNCTION reject_notification_insert();
            """);

    private sealed class CancelAfterSaveChanges(int cancelAfter, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private int _saved;

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saved) == cancelAfter)
            {
                cancellation.Cancel();
            }

            return ValueTask.FromResult(result);
        }
    }
}
