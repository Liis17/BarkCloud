using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.ResetPassword;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using DomainResetPassword = BarkCloud.Identity.Domain.ResetPassword;
using OtpType = BarkCloud.Identity.Domain.OtpType;

namespace BarkCloud.Identity.Tests.Features.ResetPassword;

public class ResetPasswordCommandHandlerTests
{
    private readonly Mock<IResetPasswordsStorage> _resets = new();
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<ResetPasswordCommandHandler> _logger = NullLogger<ResetPasswordCommandHandler>.Instance;

    public ResetPasswordCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);
        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
    }

    private ResetPasswordCommandHandler CreateSut(RequestContext? ctx = null) => new(
        _resets.Object, _authProps.Object, _usersClient.Object,
        ctx ?? FullContext(), _notifications.Object, _location.Object, _metrics, _logger);

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        IpAddress = "127.0.0.1"
    };

    [Fact]
    public async Task Handle_NoUsernameAndEmail_Throws()
    {
        var act = () => CreateSut().Handle(new ResetPasswordCommand(), default);

        await act.Should().ThrowAsync<NotSetUsernameOrEmailException>();
    }

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null };

        var act = () => CreateSut(ctx).Handle(new ResetPasswordCommand { Username = "u" }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFakeResetId()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse()));

        var response = await CreateSut().Handle(new ResetPasswordCommand { Username = "u" }, default);

        response.ResetId.Should().NotBeNullOrEmpty();
        _resets.Verify(s => s.AddResetPassword(It.IsAny<DomainResetPassword>()), Times.Never);
        _metrics.SnapshotAndReset().Should().ContainKey("password_reset_user_not_found");
    }

    [Fact]
    public async Task Handle_AuthenticatorRequestedButNotEnabled_Throws()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse { User = new User { Id = 42 } }));
        _authProps.Setup(s => s.CheckOtpEnabled(42)).ReturnsAsync(false);

        var act = () => CreateSut().Handle(
            new ResetPasswordCommand { Username = "u", OtpType = OtpType.Authenticator }, default);

        await act.Should().ThrowAsync<OtpNotCreatedException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorEnabled_CreatesResetWithoutNotification()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse { User = new User { Id = 42 } }));
        _authProps.Setup(s => s.CheckOtpEnabled(42)).ReturnsAsync(true);
        _resets.Setup(s => s.AddResetPassword(It.IsAny<DomainResetPassword>()))
            .ReturnsAsync((DomainResetPassword r) => { r.Id = Guid.NewGuid(); return r; });

        var response = await CreateSut().Handle(
            new ResetPasswordCommand { Username = "u", OtpType = OtpType.Authenticator }, default);

        response.ResetId.Should().NotBeNullOrEmpty();
        _notifications.Verify(n => n.SendNotification(It.IsAny<Notification>()), Times.Never);
        _metrics.SnapshotAndReset().Should().ContainKey("password_reset_initiated_authenticator");
    }

    [Fact]
    public async Task Handle_EmailType_SendsEmailWithCode()
    {
        _usersClient
            .Setup(c => c.FindByLoginAsync(It.IsAny<FindByLoginRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new FindByLoginResponse { User = new User { Id = 42, Username = "u" } }));
        _usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 42, Username = "u" },
                Contact = new UserContact { Email = "u@e" }
            }));
        _authProps.Setup(s => s.CheckOtpEnabled(42)).ReturnsAsync(false);
        _resets.Setup(s => s.AddResetPassword(It.IsAny<DomainResetPassword>()))
            .ReturnsAsync((DomainResetPassword r) => { r.Id = Guid.NewGuid(); return r; });

        var response = await CreateSut().Handle(
            new ResetPasswordCommand { Email = "u@e", OtpType = OtpType.Email }, default);

        response.ResetId.Should().NotBeNullOrEmpty();
        _notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.ResetPassword)), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("password_reset_initiated_email");
    }
}
