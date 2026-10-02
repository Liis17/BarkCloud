using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.SessionRevocation;
using BarkCloud.TestKit;

using Google.Protobuf.WellKnownTypes;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class GrpcRevocationFeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fetch_MapsSnapshotOrDeltaAndPassesCancellationAndDeadline(bool delta)
    {
        var now = DateTime.UtcNow;
        DateTime? since = delta ? now.AddMinutes(-1) : null;
        using var cancellation = new CancellationTokenSource();
        var response = new GetRevokedSessionsResponse { ServerTime = Timestamp.FromDateTime(now) };
        response.Sessions.Add(new RevokedSession
        {
            UserId = 42, DeviceId = "d1", RevokedAt = Timestamp.FromDateTime(now.AddSeconds(-1)),
            ExpiresAt = Timestamp.FromDateTime(now.AddHours(1))
        });
        response.Sessions.Add(new RevokedSession
        {
            UserId = 42, DeviceId = "d2", RevokedAt = Timestamp.FromDateTime(now.AddSeconds(-1)),
            ExpiresAt = Timestamp.FromDateTime(now.AddHours(1)), MaxSessionId = 17
        });
        var client = new Mock<SessionRevocationApi.SessionRevocationApiClient>();
        client.Setup(x => x.GetRevokedSessionsAsync(It.IsAny<GetRevokedSessionsRequest>(), null,
                It.IsAny<DateTime?>(), cancellation.Token))
            .Returns(GrpcCallHelpers.AsyncUnary(response));
        var sut = new GrpcRevocationFeed(client.Object);

        var batch = await sut.FetchAsync(since, cancellation.Token);

        batch.ServerTime.Should().Be(now);
        batch.Sessions.Should().Equal(
            new SessionRevocation(42, "d1", now.AddSeconds(-1), now.AddHours(1)),
            new SessionRevocation(42, "d2", now.AddSeconds(-1), now.AddHours(1), 17));
        client.Verify(x => x.GetRevokedSessionsAsync(
            It.Is<GetRevokedSessionsRequest>(r => r.ChangedSince == null ? !since.HasValue : r.ChangedSince.ToDateTime() == since),
            null, It.Is<DateTime?>(d => d >= now && d <= DateTime.UtcNow.AddSeconds(10)), cancellation.Token), Times.Once);
    }
}
