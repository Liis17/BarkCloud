using BarkCloud.Files.Consumers;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Queue.Identity;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Consumers;

public class SessionRevokedConsumerTests
{
    [Fact]
    public async Task Consume_RevokesSessionInCache()
    {
        var cache = new TokenRevocationCache();
        var sut = new SessionRevokedConsumer(cache, NullLogger<SessionRevokedConsumer>.Instance);

        var msg = new SessionRevokedEvent
        {
            UserId = 42,
            DeviceId = "d1",
            AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
        var ctx = new Mock<ConsumeContext<SessionRevokedEvent>>();
        ctx.SetupGet(c => c.Message).Returns(msg);

        await sut.Consume(ctx.Object);

        cache.IsRevoked(42, "d1", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }
}
