using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.RemoveActiveSession;
using BarkCloud.Identity.Persistence.Exceptions;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Identity;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.RemoveActiveSession;

public class RemoveActiveSessionCommandHandlerTests
{
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly JwtSettings _jwt = new() { SecretKey = "k", Issuer = "i", Audience = "a", ExpiryMinutes = 15 };
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<RemoveActiveSessionCommandHandler> _logger = NullLogger<RemoveActiveSessionCommandHandler>.Instance;

    private RemoveActiveSessionCommandHandler CreateSut() => new(
        _refreshTokens.Object,
        UserContextFactory.Create(42),
        _usersClient.Object,
        _publish.Object,
        _jwt,
        _metrics,
        _logger);

    [Fact]
    public async Task Handle_SessionNotFound_ThrowsSessionNotFound()
    {
        _refreshTokens.Setup(s => s.DeleteRefreshTokensByDeviceId("d1", 42))
            .ThrowsAsync(new RefreshTokenNotFoundException());

        var act = () => CreateSut().Handle(new RemoveActiveSessionCommand { DeviceId = "d1" }, default);

        await act.Should().ThrowAsync<SessionNotFoundException>();
    }

    [Fact]
    public async Task Handle_HappyPath_PublishesEventAndDeletesUserDevice()
    {
        _usersClient
            .Setup(c => c.DeleteUserDeviceAsync(It.IsAny<DeleteUserDeviceRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new DeleteUserDeviceResponse()));

        await CreateSut().Handle(new RemoveActiveSessionCommand { DeviceId = "d1" }, default);

        _refreshTokens.Verify(s => s.DeleteRefreshTokensByDeviceId("d1", 42), Times.Once);
        _publish.Verify(p => p.Publish(
            It.Is<SessionRevokedEvent>(e => e.UserId == 42 && e.DeviceId == "d1"),
            It.IsAny<CancellationToken>()), Times.Once);
        _usersClient.Verify(c => c.DeleteUserDeviceAsync(
            It.Is<DeleteUserDeviceRequest>(r => r.DeviceId == "d1" && r.UserId == 42),
            null, null, default), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap["sessions_removed"].Should().Be(1);
        snap["sessions_revoked"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_UsersClientFailureIsSwallowed()
    {
        _usersClient
            .Setup(c => c.DeleteUserDeviceAsync(It.IsAny<DeleteUserDeviceRequest>(), null, null, default))
            .Throws(new InvalidOperationException("users down"));

        await CreateSut().Handle(new RemoveActiveSessionCommand { DeviceId = "d1" }, default);

        _publish.Verify(p => p.Publish(It.IsAny<SessionRevokedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
