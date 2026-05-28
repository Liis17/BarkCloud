using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.EnableOtpVerification;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.EnableOtpVerification;

public class EnableOtpVerificationCommandHandlerTests
{
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<EnableOtpVerificationCommandHandler> _logger = NullLogger<EnableOtpVerificationCommandHandler>.Instance;

    public EnableOtpVerificationCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);
        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
    }

    private EnableOtpVerificationCommandHandler CreateSut(RequestContext? ctx = null, UserContext? user = null)
        => new(user ?? UserContextFactory.Create(42),
            _authProps.Object,
            _usersClient.Object,
            _notifications.Object,
            ctx ?? FullContext(),
            _location.Object,
            _metrics,
            _logger);

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        IpAddress = "127.0.0.1"
    };

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null };

        var act = () => CreateSut(ctx).Handle(new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoOperationSystem_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = null };

        var act = () => CreateSut(ctx).Handle(new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator }, default);

        await act.Should().ThrowAsync<XOsNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_NoAppInfo_Throws()
    {
        var ctx = new RequestContext { DeviceName = "d", OperationSystem = "A" };

        var act = () => CreateSut(ctx).Handle(new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator }, default);

        await act.Should().ThrowAsync<XAppInfoIsRequiedException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorType_StoresSecretAndReturnsQr()
    {
        _usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);

        var response = await CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Authenticator }, default);

        response.OtpCode.Should().NotBeNullOrEmpty();
        response.OtpQr.Should().NotBeNullOrEmpty();
        _authProps.Verify(s => s.AddUserOtpSecretKey(42, It.IsAny<string>()), Times.Once);
        _authProps.Verify(s => s.UpdateOptType(OtpType.Authenticator, 42), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("otp_setup_authenticator");
    }

    [Fact]
    public async Task Handle_EmailType_SendsNotificationAndStoresCode()
    {
        _usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));
        _usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 42, Username = "u" },
                Contact = new UserContact { Email = "u@e" }
            }));
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);

        var response = await CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Email }, default);

        response.OtpQr.Should().BeEmpty();
        _authProps.Verify(s => s.UpdateLastEmailAuthCode(42, It.IsAny<string>()), Times.Once);
        _authProps.Verify(s => s.UpdateOptType(OtpType.Email, 42), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.ConfirmationOtpEmail)), Times.Once);
    }

    [Fact]
    public async Task Handle_UnknownType_ReturnsEmptyQrWithoutSideEffects()
    {
        _usersClient
            .Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse { User = new User { Id = 42, Username = "u" } }));

        var response = await CreateSut().Handle(
            new EnableOtpVerificationCommand { OptType = OtpTypeId.Unknown }, default);

        response.OtpQr.Should().BeEmpty();
        _authProps.Verify(s => s.AddUserOtpSecretKey(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _authProps.Verify(s => s.UpdateLastEmailAuthCode(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }
}
