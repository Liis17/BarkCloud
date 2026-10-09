using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.CreateSessionForUserServer;
using BarkCloud.Identity.Host;
using BarkCloud.Proto.Identity;
using BarkCloud.TestKit;

using Grpc.Core;

using MediatR;

namespace BarkCloud.Identity.Tests.Host;

public class IdentityServerApiServiceTests
{
    [Fact]
    public async Task CreateSessionForUserServer_ForwardsCallCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateSessionForUserServerCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateSessionForUserServerResponse());
        var sut = new IdentityServerApiService(mediator.Object, new MetricsCollector());

        await sut.CreateSessionForUserServer(new CreateSessionForUserServerRequest
            {
                UserId = 7,
                DeviceId = "device-1",
                DeviceName = "Server",
                OperationSystem = "Linux",
                AppName = "BarkCloud.Web",
                IpAddress = "198.51.100.9"
            }, new TestServerCallContext(cancellationToken: cts.Token))
            .WaitAsync(TimeSpan.FromSeconds(2));

        mediator.Verify(m => m.Send(It.IsAny<CreateSessionForUserServerCommand>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task CreateSessionForUserServer_PropagatesMediatorCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateSessionForUserServerCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromCanceled<CreateSessionForUserServerResponse>(cts.Token));
        var sut = new IdentityServerApiService(mediator.Object, new MetricsCollector());

        var act = () => sut.CreateSessionForUserServer(new CreateSessionForUserServerRequest
            {
                UserId = 7,
                DeviceId = "device-1",
                DeviceName = "Server",
                OperationSystem = "Linux",
                AppName = "BarkCloud.Web",
                IpAddress = "198.51.100.9"
            }, new TestServerCallContext(cancellationToken: cts.Token))
            .WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cts.Token);
        mediator.Verify(m => m.Send(It.IsAny<CreateSessionForUserServerCommand>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task CreateSessionForUserServer_PropagatesMediatorFailure()
    {
        var failure = new RpcException(new Status(StatusCode.Unavailable, "mediator failed"));
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateSessionForUserServerCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException<CreateSessionForUserServerResponse>(failure));
        var sut = new IdentityServerApiService(mediator.Object, new MetricsCollector());

        var act = () => sut.CreateSessionForUserServer(new CreateSessionForUserServerRequest
            {
                UserId = 7,
                DeviceId = "device-1",
                DeviceName = "Server",
                OperationSystem = "Linux",
                AppName = "BarkCloud.Web",
                IpAddress = "198.51.100.9"
            }, new TestServerCallContext())
            .WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.Should().BeSameAs(failure);
    }
}
