using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.Auth;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using MediatR;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.Auth;

public class AuthCommandHandlerTests
{
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<AuthCommandHandler> _logger = NullLogger<AuthCommandHandler>.Instance;

    public AuthCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);

        _location = new Mock<LocationClient>(
            new HttpClient(),
            new MetricsCollector(),
            NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
    }

    private AuthCommandHandler CreateSut(RequestContext? ctx = null) => new(
        _usersClient.Object, _mediator.Object, _authProps.Object, _notifications.Object,
        _refreshTokens.Object, ctx ?? FullContext(), _passwords.Object, _location.Object, _metrics, _logger);

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android 14",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        DeviceId = "device-1",
        IpAddress = "127.0.0.1"
    };

    [Fact]
    public async Task Handle_NoUsernameAndEmail_Throws()
    {
        var act = () => CreateSut().Handle(new AuthCommand { Password = "p" }, default);

        await act.Should().ThrowAsync<NotSetUsernameOrEmailException>();
    }

    [Fact]
    public async Task Handle_NoPassword_Throws()
    {
        var act = () => CreateSut().Handle(new AuthCommand { Username = "u" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
    }

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null, OperationSystem = "Android", AppName = "A", AppVersion = "1" };

        var act = () => CreateSut(ctx).Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoOperationSystem_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = null, AppName = "A", AppVersion = "1" };

        var act = () => CreateSut(ctx).Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<XOsNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoAppName_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = "Android", AppName = null, AppVersion = "1" };

        var act = () => CreateSut(ctx).Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<XAppInfoIsRequiedException>();
    }

    [Fact]
    public async Task Handle_UserNotFound_ThrowsInvalidLoginOrPassword()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse()));

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
    }

    [Fact]
    public async Task Handle_UserNotFound_IncrementsFailureMetrics()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse()));

        await Assert.ThrowsAsync<InvalidLoginOrPasswordException>(
            () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default));

        var snap = _metrics.SnapshotAndReset();
        snap["auth_login_failed"].Should().Be(1);
        snap["auth_login_failed_user_not_found"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_TotpEnabledAndNoCode_ThrowsOtpCodeNeed()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse
            {
                User = new User { Id = 1, Username = "u" }
            }));
        _authProps
            .Setup(s => s.GetUserAuthProperties(1))
            .ReturnsAsync(new AuthUserProperty { UserId = 1, OtpEnabled = true });

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "p" }, default);

        await act.Should().ThrowAsync<OtpCodeNeedException>();
    }

    [Fact]
    public async Task Handle_WrongPassword_ThrowsInvalidLoginOrPassword()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse
            {
                User = new User { Id = 1, Username = "u" }
            }));
        _usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 1, Username = "u" },
                Contact = new UserContact { Email = "u@e" }
            }));
        _authProps.Setup(s => s.GetUserAuthProperties(1)).ReturnsAsync((AuthUserProperty?)null);
        _passwords.Setup(s => s.GetUserPasswordHash(1))
            .ReturnsAsync(BCrypt.Net.BCrypt.HashPassword("correct-password"));

        var act = () => CreateSut().Handle(new AuthCommand { Username = "u", Password = "wrong" }, default);

        await act.Should().ThrowAsync<InvalidLoginOrPasswordException>();
    }

    [Fact]
    public async Task Handle_HappyPath_ReturnsRefreshAndAccessTokens()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse
            {
                User = new User { Id = 42, Username = "u" }
            }));
        _usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 42, Username = "u" },
                Contact = new UserContact { Email = "u@e" }
            }));
        _usersClient
            .Setup(c => c.RegisterDeviceAsync(It.IsAny<RegisterDeviceRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new RegisterDeviceResponse()));

        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);
        _passwords.Setup(s => s.GetUserPasswordHash(42))
            .ReturnsAsync(BCrypt.Net.BCrypt.HashPassword("right"));

        _mediator
            .Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTokenResponse
            {
                AccessToken = new Token { Value = "access" }
            });

        var response = await CreateSut().Handle(new AuthCommand { Username = "u", Password = "right" }, default);

        response.AccessToken.Value.Should().Be("access");
        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        _refreshTokens.Verify(s => s.DeleteRefreshTokensByDeviceIdSafe("device-1", 42), Times.Once);
        _refreshTokens.Verify(
            s => s.CreateNewRefreshToken(It.IsAny<string>(), 42, "device-1", It.IsAny<int>()),
            Times.Once);
        _notifications.Verify(n => n.SendNotification(It.IsAny<EmailNotification>()), Times.AtLeastOnce);
    }
}
