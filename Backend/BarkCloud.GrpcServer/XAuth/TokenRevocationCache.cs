using System.Collections.Concurrent;

namespace BarkCloud.GrpcServer.XAuth;

public class TokenRevocationCache
{
    private readonly ConcurrentDictionary<string, RevocationEntry> _revokedSessions = new();

    public void Revoke(long userId, string deviceId, DateTime accessTokenExpiresAt)
        => Revoke(userId, deviceId, DateTime.UtcNow, accessTokenExpiresAt);

    public void Revoke(long userId, string deviceId, DateTime revokedAt, DateTime expiresAt)
    {
        var key = BuildKey(userId, deviceId);
        var incoming = new RevocationEntry(revokedAt, expiresAt);
        _revokedSessions.AddOrUpdate(key, incoming, (_, existing) => new RevocationEntry(
            existing.RevokedAt > incoming.RevokedAt ? existing.RevokedAt : incoming.RevokedAt,
            existing.ExpiresAt > incoming.ExpiresAt ? existing.ExpiresAt : incoming.ExpiresAt));
    }

    /// <summary>
    /// Сессия отозвана, только если токен выдан <b>не позже</b> момента отзыва.
    /// Токен, выданный после отзыва (новый логин с тем же устройством), считается
    /// валидным — иначе повторный вход после logout ловит 401 до истечения записи.
    /// </summary>
    public bool IsRevoked(long userId, string deviceId, DateTime tokenIssuedAt)
    {
        var key = BuildKey(userId, deviceId);
        if (_revokedSessions.TryGetValue(key, out var entry))
        {
            return tokenIssuedAt <= entry.RevokedAt;
        }

        return false;
    }

    public void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _revokedSessions)
        {
            if (kvp.Value.ExpiresAt < now)
            {
                _revokedSessions.TryRemove(kvp);
            }
        }
    }

    private static string BuildKey(long userId, string deviceId) => $"{userId}:{deviceId}";

    /// <param name="RevokedAt">Момент отзыва — токены с iat не позже него считаются отозванными.</param>
    /// <param name="ExpiresAt">Когда запись можно удалить (старые токены к этому времени уже истекли).</param>
    private readonly record struct RevocationEntry(DateTime RevokedAt, DateTime ExpiresAt);
}
