namespace BarkCloud.GrpcServer.XAuth;

public interface IRevocationFeed
{
    Task<RevocationBatch> FetchAsync(DateTime? changedSince, CancellationToken cancellationToken);
}

public sealed record SessionRevocation(long UserId, string DeviceId, DateTime RevokedAt, DateTime ExpiresAt);

public sealed record RevocationBatch(IReadOnlyList<SessionRevocation> Sessions, DateTime ServerTime);
