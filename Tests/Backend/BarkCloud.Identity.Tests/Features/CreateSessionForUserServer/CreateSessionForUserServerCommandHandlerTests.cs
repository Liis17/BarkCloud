using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.CreateSessionForUserServer;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;

using Grpc.Core;

using MediatR;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.CreateSessionForUserServer;

public class CreateSessionForUserServerCommandHandlerTests : IDisposable
{
    private readonly Mock<SessionIssuer> _sessions;
    private readonly MetricsCollector _metrics = new();
    private readonly SqliteIdentityContext _database = new();

    public void Dispose() => _database.Dispose();

    public CreateSessionForUserServerCommandHandlerTests()
    {
        _sessions = new Mock<SessionIssuer>(
            Mock.Of<UsersServerApi.UsersServerApiClient>(), Mock.Of<IMediator>(), Mock.Of<INotificationOutbox>(),
            Mock.Of<IRefreshTokensStorage>(), _database.Context, new RequestContext(),
            new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance).Object,
            _metrics, NullLogger<SessionIssuer>.Instance);
        _sessions
            .Setup(s => s.IssueAsync(It.IsAny<long>(), It.IsAny<SessionDevice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResponse
            {
                AccessToken = new Token { Value = "at" },
                RefreshToken = new Token { Value = "rt" }
            });
    }

    private CreateSessionForUserServerCommandHandler CreateSut() => new(
        _sessions.Object, _metrics, NullLogger<CreateSessionForUserServerCommandHandler>.Instance);

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
        _sessions.Verify(
            s => s.IssueAsync(It.IsAny<long>(), It.IsAny<SessionDevice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ValidRequest_IssuesSessionForRequestedDeviceAndReturnsTokens()
    {
        using var cts = new CancellationTokenSource();

        var response = await CreateSut().Handle(ValidCommand(), cts.Token);

        response.AccessToken.Value.Should().Be("at");
        response.RefreshToken.Value.Should().Be("rt");
        _sessions.Verify(s => s.IssueAsync(
            7, new SessionDevice("d1", "Pixel", "Android", "BarkCloud", "1.1.1.1"), cts.Token), Times.Once);
    }

    [Fact]
    public async Task Handle_ValidRequest_CountsServerSession()
    {
        await CreateSut().Handle(ValidCommand(), default);

        _metrics.SnapshotAndReset()["server_sessions_created"].Should().Be(1);
    }
}
