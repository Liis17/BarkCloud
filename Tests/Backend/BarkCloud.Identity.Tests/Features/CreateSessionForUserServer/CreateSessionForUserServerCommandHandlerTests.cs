using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.CreateSessionForUserServer;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using Grpc.Core;

using MassTransit;

using MediatR;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.CreateSessionForUserServer;

public class CreateSessionForUserServerCommandHandlerTests
{
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<NotificationQueueSender> _notifications;
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<LocationClient> _location;
    private readonly MetricsCollector _metrics = new();

    public CreateSessionForUserServerCommandHandlerTests()
    {
        _notifications = new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>());
        _notifications.Setup(n => n.SendNotification(It.IsAny<Notification>())).Returns(Task.CompletedTask);

        _location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
        _location.Setup(c => c.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);

        _mediator.Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTokenResponse { AccessToken = new Token { Value = "at" } });

        _usersClient
            .Setup(c => c.RegisterDeviceAsync(It.IsAny<RegisterDeviceRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new RegisterDeviceResponse()));
        _usersClient
            .Setup(c => c.GetUserContactsAsync(It.IsAny<GetUserContactsRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetUserContactsResponse
            {
                User = new User { Id = 7, Username = "barker" },
                Contact = new UserContact { Email = "u@e" }
            }));
    }

    private CreateSessionForUserServerCommandHandler CreateSut() => new(
        _usersClient.Object,
        _mediator.Object,
        _notifications.Object,
        _refreshTokens.Object,
        _location.Object,
        _metrics,
        NullLogger<CreateSessionForUserServerCommandHandler>.Instance);

    private static CreateSessionForUserServerCommand ValidCommand() => new()
    {
        UserId = 7, DeviceId = "d1", DeviceName = "Pixel",
        OperationSystem = "Android", AppName = "BarkCloud", IpAddress = "1.1.1.1"
    };

    [Theory]
    [InlineData(0, "d1", "Pixel", "Android", "BarkCloud")]
    [InlineData(7, "", "Pixel", "Android", "BarkCloud")]
    [InlineData(7, "d1", "", "Android", "BarkCloud")]
    [InlineData(7, "d1", "Pixel", "", "BarkCloud")]
    [InlineData(7, "d1", "Pixel", "Android", "")]
    public async Task Handle_InvalidArguments_ThrowsRpcException(
        long userId, string deviceId, string deviceName, string os, string appName)
    {
        var act = () => CreateSut().Handle(new CreateSessionForUserServerCommand
        {
            UserId = userId, DeviceId = deviceId, DeviceName = deviceName,
            OperationSystem = os, AppName = appName
        }, default);

        await act.Should().ThrowAsync<RpcException>();
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ValidRequest_CreatesSessionAndReturnsTokens()
    {
        var response = await CreateSut().Handle(ValidCommand(), default);

        response.AccessToken.Value.Should().Be("at");
        response.RefreshToken.Value.Should().NotBeNullOrEmpty();
        _refreshTokens.Verify(s => s.DeleteRefreshTokensByDeviceIdSafe("d1", 7), Times.Once);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 7, "d1", It.IsAny<int>()), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap["server_sessions_created"].Should().Be(1);
        snap["sessions_created"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_ValidRequest_RegistersDeviceAndSendsLoginNotification()
    {
        await CreateSut().Handle(ValidCommand(), default);

        _usersClient.Verify(c => c.RegisterDeviceAsync(
            It.Is<RegisterDeviceRequest>(r => r.DeviceId == "d1" && r.UserId == 7),
            null, null, default), Times.Once);
        _notifications.Verify(n => n.SendNotification(It.Is<EmailNotification>(
            e => e.Type == NotificationType.SuccessfulLogin && e.Address == "u@e")), Times.Once);
    }
}
