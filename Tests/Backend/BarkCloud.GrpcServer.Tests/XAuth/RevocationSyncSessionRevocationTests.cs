using BarkCloud.GrpcServer.XAuth;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class RevocationSyncSessionRevocationTests
{
    [Fact]
    public async Task Start_PassesMaxSessionIdToCache_RevokingOldSessionButNotNewOne()
    {
        var now = DateTime.UtcNow;
        var feed = new Mock<IRevocationFeed>();
        feed.Setup(x => x.FetchAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevocationBatch([new SessionRevocation(42, "d1", now, now.AddHours(1), 5)], now));
        var cache = new TokenRevocationCache();
        using var sut = new RevocationSyncService(cache, feed.Object, NullLogger<RevocationSyncService>.Instance, new FakeTimeProvider());

        await sut.StartAsync(default);

        cache.IsRevoked(42, "d1", now.AddMinutes(1), sessionId: 5).Should().BeTrue();
        cache.IsRevoked(42, "d1", now.AddMinutes(-1), sessionId: 6).Should().BeFalse();
        await sut.StopAsync(default);
    }
}
