using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Queue.Identity;
using BarkCloud.Users.Consumers;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Consumers;

public class SessionRevokedConsumerTests
{
    [Fact]
    public async Task Consume_RevokesSessionAndRecordsMetrics()
    {
        var cache = new TokenRevocationCache();
        var metrics = new MetricsCollector();
        var sut = new SessionRevokedConsumer(cache, metrics, NullLogger<SessionRevokedConsumer>.Instance);

        var msg = new SessionRevokedEvent
        {
            UserId = 42,
            DeviceId = "d1",
            AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
        var ctx = new Mock<ConsumeContext<SessionRevokedEvent>>();
        ctx.SetupGet(c => c.Message).Returns(msg);

        await sut.Consume(ctx.Object);

        cache.IsRevoked(42, "d1").Should().BeTrue();
        var snap = metrics.SnapshotAndReset();
        snap["session_revoked_received"].Should().Be(1);
        snap.Should().ContainKey("last_session_revoked_unix");
    }
}
