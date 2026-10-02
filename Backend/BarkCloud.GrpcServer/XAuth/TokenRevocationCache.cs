using System.Collections.Concurrent;

namespace BarkCloud.GrpcServer.XAuth;

public class TokenRevocationCache
{
    private readonly ConcurrentDictionary<string, RevocationEntry> _revokedSessions = new();

    /// <param name="maxSessionId">Задан — отзыв по сессии: <paramref name="revokedAt"/> порогом времени не становится.</param>
    public void Revoke(long userId, string deviceId, DateTime revokedAt, DateTime expiresAt, long? maxSessionId = null)
    {
        var key = BuildKey(userId, deviceId);
        var incoming = maxSessionId.HasValue
            ? new RevocationEntry(null, maxSessionId, expiresAt)
            : new RevocationEntry(revokedAt, null, expiresAt);
        _revokedSessions.AddOrUpdate(key, incoming, (_, existing) => new RevocationEntry(
            Max(existing.RevokedAt, incoming.RevokedAt),
            Max(existing.MaxSessionId, incoming.MaxSessionId),
            existing.ExpiresAt > incoming.ExpiresAt ? existing.ExpiresAt : incoming.ExpiresAt));
    }

    /// <summary>
    /// Отзыв по времени: токен выдан <b>не позже</b> момента отзыва. Токен, выданный после отзыва
    /// (новый логин с тем же устройством), считается валидным — иначе повторный вход после logout
    /// ловит 401 до истечения записи.
    /// Отзыв по сессии (сброс пароля на том же устройстве): <paramref name="sessionId"/> не больше порога.
    /// Токен без sid при такой записи отозван (fail-safe: он выдан до появления sid, т.е. раньше сброса).
    /// </summary>
    public bool IsRevoked(long userId, string deviceId, DateTime tokenIssuedAt, long? sessionId = null)
    {
        var key = BuildKey(userId, deviceId);
        if (!_revokedSessions.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.RevokedAt.HasValue && tokenIssuedAt <= entry.RevokedAt.Value)
        {
            return true;
        }

        return entry.MaxSessionId.HasValue && (!sessionId.HasValue || sessionId.Value <= entry.MaxSessionId.Value);
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

    private static T? Max<T>(T? a, T? b) where T : struct, IComparable<T>
        => !a.HasValue ? b : !b.HasValue ? a : a.Value.CompareTo(b.Value) >= 0 ? a : b;

    /// <param name="RevokedAt">Порог времени — токены с iat не позже него считаются отозванными; нет — отзыва по времени нет.</param>
    /// <param name="MaxSessionId">Порог сессии — токены с sid не больше него (и без sid) считаются отозванными; нет — отзыва по сессии нет.</param>
    /// <param name="ExpiresAt">Когда запись можно удалить (старые токены к этому времени уже истекли).</param>
    private readonly record struct RevocationEntry(DateTime? RevokedAt, long? MaxSessionId, DateTime ExpiresAt);
}
