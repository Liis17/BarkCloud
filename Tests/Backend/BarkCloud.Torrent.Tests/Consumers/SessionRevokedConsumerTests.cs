using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Queue.Identity;
using BarkCloud.Torrent.Consumers;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Torrent.Tests.Consumers;

public class SessionRevokedConsumerTests
{
    private static Mock<ConsumeContext<SessionRevokedEvent>> Context(long userId, string deviceId)
    {
        var ctx = new Mock<ConsumeContext<SessionRevokedEvent>>();
        ctx.SetupGet(c => c.Message).Returns(new SessionRevokedEvent
        {
            UserId = userId,
            DeviceId = deviceId,
            AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(15)
        });
        return ctx;
    }

    [Fact]
    public async Task Consume_RevokesSessionAndRecordsMetrics()
    {
        var cache = new TokenRevocationCache();
        var metrics = new MetricsCollector();
        var sut = new SessionRevokedConsumer(cache, metrics, NullLogger<SessionRevokedConsumer>.Instance);

        await sut.Consume(Context(42, "d1").Object);

        cache.IsRevoked(42, "d1", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
        var snap = metrics.SnapshotAndReset();
        snap["session_revoked_received"].Should().Be(1);
        snap.Should().ContainKey("last_session_revoked_unix");
    }

    [Fact]
    public async Task Consume_DoesNotRevokeOtherDeviceOrUser()
    {
        var cache = new TokenRevocationCache();
        var sut = new SessionRevokedConsumer(cache, new MetricsCollector(), NullLogger<SessionRevokedConsumer>.Instance);

        await sut.Consume(Context(42, "d1").Object);

        cache.IsRevoked(42, "d2", DateTime.UtcNow.AddMinutes(-1)).Should().BeFalse();
        cache.IsRevoked(43, "d1", DateTime.UtcNow.AddMinutes(-1)).Should().BeFalse();
    }
}
