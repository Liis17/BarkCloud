using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.RemoveActiveSessionServer;
using BarkCloud.Identity.Persistence.Exceptions;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.TestKit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.RemoveActiveSessionServer;

public class RemoveActiveSessionServerCommandHandlerTests
{
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();
    private readonly MetricsCollector _metrics = new();

    private RemoveActiveSessionServerCommandHandler CreateSut() => new(
        _refreshTokens.Object,
        _usersClient.Object,
        _metrics,
        NullLogger<RemoveActiveSessionServerCommandHandler>.Instance);

    [Fact]
    public async Task Handle_SessionNotFound_ThrowsSessionNotFound()
    {
        _refreshTokens.Setup(s => s.RevokeSession("d1", 7, default))
            .ThrowsAsync(new RefreshTokenNotFoundException());

        var act = () => CreateSut().Handle(new RemoveActiveSessionServerCommand { UserId = 7, DeviceId = "d1" }, default);

        await act.Should().ThrowAsync<SessionNotFoundException>();
    }

    [Fact]
    public async Task Handle_HappyPath_RevokesSessionAndDeletesUserDevice()
    {
        _usersClient
            .Setup(c => c.DeleteUserDeviceAsync(It.IsAny<DeleteUserDeviceRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new DeleteUserDeviceResponse()));

        await CreateSut().Handle(new RemoveActiveSessionServerCommand { UserId = 7, DeviceId = "d1" }, default);

        _refreshTokens.Verify(s => s.RevokeSession("d1", 7, default), Times.Once);
        _usersClient.Verify(c => c.DeleteUserDeviceAsync(
            It.Is<DeleteUserDeviceRequest>(r => r.DeviceId == "d1" && r.UserId == 7),
            null, null, default), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap["server_sessions_removed"].Should().Be(1);
        snap["sessions_revoked"].Should().Be(1);
    }

    [Fact]
    public async Task Handle_UsersClientFailureIsSwallowed()
    {
        _usersClient
            .Setup(c => c.DeleteUserDeviceAsync(It.IsAny<DeleteUserDeviceRequest>(), null, null, default))
            .Throws(new InvalidOperationException("users down"));

        await CreateSut().Handle(new RemoveActiveSessionServerCommand { UserId = 7, DeviceId = "d1" }, default);

        _refreshTokens.Verify(s => s.RevokeSession("d1", 7, default), Times.Once);
    }
}
