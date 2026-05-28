using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.Logout;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Identity;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.Logout;

public class LogoutCommandHandlerTests
{
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly JwtSettings _jwt = new() { SecretKey = "k", Issuer = "i", Audience = "a", ExpiryMinutes = 15 };
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<LogoutCommandHandler> _logger = NullLogger<LogoutCommandHandler>.Instance;

    private LogoutCommandHandler CreateSut() => new(
        _refreshTokens.Object,
        UserContextFactory.Create(42, deviceId: "device-1"),
        _usersClient.Object,
        _publish.Object,
        _jwt,
        _metrics,
        _logger);

    [Fact]
    public async Task Handle_HappyPath_DeletesTokensAndPublishesEvent()
    {
        _usersClient
            .Setup(c => c.DeleteUserDeviceAsync(It.IsAny<DeleteUserDeviceRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new DeleteUserDeviceResponse()));

        await CreateSut().Handle(new LogoutCommand(), default);

        _refreshTokens.Verify(s => s.DeleteRefreshTokensByDeviceIdSafe("device-1", 42), Times.Once);
        _publish.Verify(p => p.Publish(
            It.Is<SessionRevokedEvent>(e => e.UserId == 42 && e.DeviceId == "device-1"),
            It.IsAny<CancellationToken>()), Times.Once);
        _usersClient.Verify(c => c.DeleteUserDeviceAsync(
            It.Is<DeleteUserDeviceRequest>(r => r.UserId == 42 && r.DeviceId == "device-1"),
            null, null, default), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap["logouts"].Should().Be(1);
        snap["sessions_revoked"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_DeleteUserDeviceThrows_StillCompletesSuccessfully()
    {
        _usersClient
            .Setup(c => c.DeleteUserDeviceAsync(It.IsAny<DeleteUserDeviceRequest>(), null, null, default))
            .Throws(new InvalidOperationException("users service down"));

        await CreateSut().Handle(new LogoutCommand(), default);

        _refreshTokens.Verify(s => s.DeleteRefreshTokensByDeviceIdSafe("device-1", 42), Times.Once);
        _publish.Verify(p => p.Publish(It.IsAny<SessionRevokedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _metrics.SnapshotAndReset()["logouts"].Should().Be(1);
    }
}
