using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Shared.Exceptions.Identity;

namespace BarkCloud.Identity.Services;

public class AuthRateLimiter(IAttemptCountersStorage storage, RequestContext requestContext, ILogger<AuthRateLimiter> logger)
    : IAuthRateLimiter
{
    public async Task EnsureSourceAsync(AuthLimits.Policy policy)
    {
        var sourceIp = requestContext.SourceIp;

        if (string.IsNullOrEmpty(sourceIp))
        {
            logger.LogWarning("Источник запроса неизвестен — лимит {Scope} по адресу не применён", policy.Scope);
            return;
        }

        var reservation = await storage.TryReserve(Key(policy, $"ip:{sourceIp}"), policy.Max, policy.Window);

        if (!reservation.Allowed)
        {
            logger.LogWarning("Лимит {Scope} для источника {SourceIp} исчерпан", policy.Scope, sourceIp);
            throw new TooManyRequestsException(reservation.RetryAfter);
        }
    }

    public async Task<bool> TryReserveAsync(AuthLimits.Policy policy, string subject)
    {
        var reservation = await storage.TryReserve(Key(policy, subject), policy.Max, policy.Window);

        return reservation.Allowed;
    }

    public async Task EnsureTotpAttemptAsync(long userId)
    {
        var policy = AuthLimits.OtpByAccount;
        var reservation = await storage.TryReserve(Key(policy, userId.ToString()), policy.Max, policy.Window);

        if (!reservation.Allowed)
        {
            logger.LogWarning("Лимит {Scope} для пользователя {UserId} исчерпан", policy.Scope, userId);
            throw new TooManyRequestsException(reservation.RetryAfter);
        }
    }

    public Task ResetAsync(AuthLimits.Policy policy, string subject, CancellationToken cancellationToken = default) =>
        storage.Reset(Key(policy, subject), cancellationToken);

    private static string Key(AuthLimits.Policy policy, string subject) => $"{policy.Scope}:{subject}";
}
