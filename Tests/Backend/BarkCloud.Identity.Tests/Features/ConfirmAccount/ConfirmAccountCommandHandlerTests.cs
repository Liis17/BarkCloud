using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ConfirmAccount;
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

namespace BarkCloud.Identity.Tests.Features.ConfirmAccount;

public class ConfirmAccountCommandHandlerTests
{
    private readonly Mock<IConfirmationCodesStorage> _codes = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<ConfirmAccountCommandHandler> _logger = NullLogger<ConfirmAccountCommandHandler>.Instance;

    public ConfirmAccountCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);
        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
    }

    private ConfirmAccountCommandHandler CreateSut(RequestContext? ctx = null) => new(
        _codes.Object, _usersClient.Object, _refreshTokens.Object, ctx ?? FullContext(),
        _notifications.Object, _location.Object, _metrics, _logger);

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        DeviceId = "device-1",
        IpAddress = "1.1.1.1"
    };

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null };

        var act = () => CreateSut(ctx).Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "0" }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_CodeNotFound_Throws()
    {
        _codes.Setup(s => s.GetCode(It.IsAny<Guid>())).ReturnsAsync((ConfirmationCode?)null);

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "0" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeNotFoundException>();
    }

    [Fact]
    public async Task Handle_CodeOfWrongType_ThrowsNotFound()
    {
        _codes.Setup(s => s.GetCode(It.IsAny<Guid>())).ReturnsAsync(new ConfirmationCode
        {
            Type = ConfirmationCodeType.ResetPassword,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Value = "0"
        });

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "0" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeNotFoundException>();
    }

    [Fact]
    public async Task Handle_CodeExpired_Throws()
    {
        _codes.Setup(s => s.GetCode(It.IsAny<Guid>())).ReturnsAsync(new ConfirmationCode
        {
            Type = ConfirmationCodeType.Registration,
            Expires = DateTime.UtcNow.AddMinutes(-1),
            Value = "0"
        });

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "0" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeExpiredException>();
    }

    [Fact]
    public async Task Handle_IncorrectCode_Throws()
    {
        _codes.Setup(s => s.GetCode(It.IsAny<Guid>())).ReturnsAsync(new ConfirmationCode
        {
            Type = ConfirmationCodeType.Registration,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Value = "12345",
            OwnerId = 1
        });

        var act = () => CreateSut().Handle(new ConfirmAccountCommand { CodeId = Guid.NewGuid().ToString(), Code = "wrong" }, default);

        await act.Should().ThrowAsync<ConfirmationCodeIncorrectException>();
    }

    [Fact]
    public async Task Handle_HappyPath_ConfirmsUserDeletesCodeAndReturnsRefresh()
    {
        var codeId = Guid.NewGuid();
        _codes.Setup(s => s.GetCode(codeId)).ReturnsAsync(new ConfirmationCode
        {
            Id = codeId,
            Type = ConfirmationCodeType.Registration,
            Expires = DateTime.UtcNow.AddHours(1),
            Value = "123456",
            OwnerId = 42
        });
        _usersClient
            .Setup(c => c.ConfirmUserAsync(It.IsAny<ConfirmUserRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new ConfirmUserResponse()));
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

        var response = await CreateSut().Handle(
            new ConfirmAccountCommand { CodeId = codeId.ToString(), Code = "123456" },
            default);

        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        _usersClient.Verify(c => c.ConfirmUserAsync(It.Is<ConfirmUserRequest>(r => r.UserId == 42), null, null, default), Times.Once);
        _codes.Verify(s => s.DeleteCode(codeId), Times.Once);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 42, "device-1", It.IsAny<int>()), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap.Should().ContainKey("accounts_confirmed");
        snap.Should().ContainKey("sessions_created");
    }
}
