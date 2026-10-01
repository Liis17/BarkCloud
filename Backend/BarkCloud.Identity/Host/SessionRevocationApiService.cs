using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.SessionRevocation;
using BarkCloud.Shared.Identity;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Identity.Host;

[Authorize(Policy = nameof(TokenType.Service))]
public class SessionRevocationApiService(IRevocationFeed feed) : SessionRevocationApi.SessionRevocationApiBase
{
    public override async Task<GetRevokedSessionsResponse> GetRevokedSessions(
        GetRevokedSessionsRequest request, ServerCallContext context)
    {
        var batch = await feed.FetchAsync(request.ChangedSince?.ToDateTime(), context.CancellationToken);
        var response = new GetRevokedSessionsResponse { ServerTime = Timestamp.FromDateTime(batch.ServerTime) };
        response.Sessions.AddRange(batch.Sessions.Select(x => new RevokedSession
        {
            UserId = x.UserId,
            DeviceId = x.DeviceId,
            RevokedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(x.RevokedAt, DateTimeKind.Utc)),
            ExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(x.ExpiresAt, DateTimeKind.Utc))
        }));
        return response;
    }
}
