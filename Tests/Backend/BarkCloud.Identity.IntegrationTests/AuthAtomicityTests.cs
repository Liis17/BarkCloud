using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.Auth;
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

using MassTransit;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.IntegrationTests;

public class AuthAtomicityTests
{
    [Fact]
    public async Task Auth_EmailLoginNotificationFailure_RetainsCodeAndAccountAttempt()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new PasswordsStorage(setup).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("password"));
            await new RefreshTokensStorage(setup, new JwtSettings { ExpiryMinutes = 60 })
                .CreateNewRefreshToken("old-refresh", 42, "device-1", 1);
            setup.AuthUserProperties.Add(new AuthUserProperty
            {
                UserId = 42,
                EmailOtpEnabled = true,
                LastEmailAuthCode = "123456",
                EmailAuthCodePurpose = EmailAuthCodePurpose.Login,
                EmailAuthCodeIssuedAt = DateTime.UtcNow,
                EmailAuthCodeExpiresAt = DateTime.UtcNow.AddMinutes(5)
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
        var handler = CreateHandler(context);

        await FluentActions.Awaiting(() => handler.Handle(
                new AuthCommand { Username = "user", Password = "password", OtpCode = "123456" }, default))
            .Should().ThrowAsync<DbUpdateException>();
        await context.SaveChangesAsync();

        await using var observer = database.CreateContext();
        var properties = await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42);
        properties.LastEmailAuthCode.Should().Be("123456");
        properties.EmailAuthCodeAttempts.Should().Be(1);
        (await observer.AuthAttemptCounters.SingleAsync(x => x.Key == "login:42")).Count.Should().Be(1);
        (await new RefreshTokensStorage(observer, new JwtSettings { ExpiryMinutes = 60 }).GetRefreshTokens(42))
            .Select(x => x.Value).Should().Equal("old-refresh");
        (await observer.PendingNotifications.AnyAsync()).Should().BeFalse();
    }

    private static AuthCommandHandler CreateHandler(IdentityContext context)
    {
        var metrics = new MetricsCollector();
        var requestContext = new RequestContext
        {
            SourceIp = "203.0.113.5", IpAddress = "198.51.100.5", DeviceId = "device-1",
            DeviceName = "Phone", OperationSystem = "Android", AppName = "BarkCloud", AppVersion = "1.0"
        };
        var jwtSettings = new JwtSettings
        {
            SecretKey = "supersecretkey_at_least_32_chars_long_for_hs256!!",
            Issuer = "bark", Audience = "bark", ExpiryMinutes = 60
        };
        var refreshTokens = new RefreshTokensStorage(context, jwtSettings);
        var outbox = new NotificationOutbox(context, new ConfigurationBuilder().Build(), metrics,
            NullLogger<NotificationOutbox>.Instance);
        var createToken = new CreateTokenCommandHandler(refreshTokens, new JwtService(jwtSettings), metrics,
            NullLogger<CreateTokenCommandHandler>.Instance);
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .Returns((CreateTokenCommand command, CancellationToken cancellationToken) =>
                createToken.Handle(command, cancellationToken));

        var users = new Mock<UsersServerApi.UsersServerApiClient>();
        users.Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse { User = new User { Id = 42, Username = "user" } }));
        users.Setup(c => c.RegisterDeviceAsync(It.IsAny<RegisterDeviceRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new RegisterDeviceResponse()));

        var location = new Mock<LocationClient>(new HttpClient(), metrics, NullLogger<LocationClient>.Instance);
        location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
        var notificationSender = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(), new ConfigurationBuilder().Build());
        var sessionIssuer = new SessionIssuer(users.Object, mediator.Object, outbox, refreshTokens, context,
            requestContext, location.Object, metrics, NullLogger<SessionIssuer>.Instance);
        var rateLimiter = new AuthRateLimiter(new AttemptCountersStorage(context), requestContext,
            NullLogger<AuthRateLimiter>.Instance);

        return new AuthCommandHandler(users.Object, new AuthPropertiesStorage(context), notificationSender.Object,
            outbox, sessionIssuer, requestContext, new PasswordsStorage(context), location.Object, metrics,
            rateLimiter, NullLogger<AuthCommandHandler>.Instance);
    }
}
