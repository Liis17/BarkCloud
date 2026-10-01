using BarkCloud.Proto.SessionRevocation;

using Google.Protobuf.WellKnownTypes;

namespace BarkCloud.GrpcServer.XAuth;

public class GrpcRevocationFeed(SessionRevocationApi.SessionRevocationApiClient client) : IRevocationFeed
{
    public async Task<RevocationBatch> FetchAsync(DateTime? changedSince, CancellationToken cancellationToken)
    {
        var request = new GetRevokedSessionsRequest();
        if (changedSince.HasValue)
        {
            request.ChangedSince = Timestamp.FromDateTime(changedSince.Value);
        }

        using var call = client.GetRevokedSessionsAsync(request,
            deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        var response = await call.ResponseAsync;
        return new RevocationBatch(response.Sessions.Select(x => new SessionRevocation(
            x.UserId, x.DeviceId, x.RevokedAt.ToDateTime(), x.ExpiresAt.ToDateTime())).ToList(),
            response.ServerTime.ToDateTime());
    }
}
