using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Services;

public class DbRevocationFeed(IServiceScopeFactory scopeFactory) : IRevocationFeed
{
    public async Task<RevocationBatch> FetchAsync(DateTime? changedSince, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityContext>();
        // Время берём до чтения: коммит после SELECT попадёт в следующий запрос.
        var serverTime = DateTime.UtcNow;
        var query = context.RevokedSessions.AsNoTracking().Where(x => x.ExpiresAt > serverTime);
        if (changedSince.HasValue)
        {
            query = query.Where(x => x.RevokedAt >= changedSince.Value);
        }

        var sessions = await query
            .Select(x => new SessionRevocation(x.UserId, x.DeviceId, x.RevokedAt, x.ExpiresAt, x.MaxSessionId))
            .ToListAsync(cancellationToken);
        return new RevocationBatch(sessions, serverTime);
    }
}
