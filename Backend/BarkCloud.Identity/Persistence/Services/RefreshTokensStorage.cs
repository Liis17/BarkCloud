using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Exceptions;
using BarkCloud.Identity.Settings;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Services;

public class RefreshTokensStorage(IdentityContext context, JwtSettings jwtSettings) : IRefreshTokensStorage
{
    public async Task<RefreshToken?> FindRefreshToken(string refreshToken)
    {
        var refreshTokenEntity = await context.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Value == refreshToken);

        return refreshTokenEntity;
    }

    public async Task<RefreshToken?> CreateNewRefreshToken(string refreshToken, long userId, string deviceId, int expiresDays,
        CancellationToken cancellationToken = default)
    {
        var refreshTokenEntity = new RefreshToken()
        {
            CreatedAt = DateTime.UtcNow,
            DeviceId = deviceId,
            ExpiresAt = DateTime.UtcNow.AddDays(expiresDays),
            UserId = userId,
            Value = refreshToken
        };

        var token = await context.RefreshTokens.AddAsync(refreshTokenEntity, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        return token.Entity;
    }

    public async Task<List<RefreshToken>> GetRefreshTokens(long userId)
    {
        return await context.RefreshTokens.Where(x => x.UserId == userId).ToListAsync();
    }

    /// <summary>
    /// Удаляет все refresh токены для устройства. Не выбрасывает исключение если токенов нет.
    /// Используется при логине для очистки старых токенов перед созданием нового.
    /// </summary>
    public async Task DeleteRefreshTokensByDeviceIdSafe(string deviceId, long userId)
    {
        var refreshTokens = await context.RefreshTokens
            .Where(x => x.DeviceId == deviceId && x.UserId == userId)
            .ToListAsync();

        if (refreshTokens.Count > 0)
        {
            context.RefreshTokens.RemoveRange(refreshTokens);
            await context.SaveChangesAsync();
        }
    }

    public Task RevokeSession(string deviceId, long userId, CancellationToken cancellationToken = default)
        => RevokeDevice(deviceId, userId, requireRefreshToken: true, cancellationToken);

    public Task RevokeSessionSafe(string deviceId, long userId, CancellationToken cancellationToken = default)
        => RevokeDevice(deviceId, userId, requireRefreshToken: false, cancellationToken);

    private async Task RevokeDevice(string deviceId, long userId, bool requireRefreshToken, CancellationToken cancellationToken)
    {
        var refreshTokens = await context.RefreshTokens
            .Where(x => x.UserId == userId && x.DeviceId == deviceId)
            .ToListAsync(cancellationToken);

        if (requireRefreshToken && refreshTokens.Count == 0)
        {
            throw new RefreshTokenNotFoundException();
        }

        await SaveRevocations(userId, refreshTokens, [deviceId], cancellationToken);
    }

    public async Task<int> RevokeAllSessions(long userId, string? currentDeviceId = null, CancellationToken cancellationToken = default)
    {
        var refreshTokens = await context.RefreshTokens
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);
        var deviceIds = refreshTokens.Select(x => x.DeviceId).Distinct().ToList();

        // Все прежние refresh удаляются, включая текущий: сброс пароля затем выдаёт текущему устройству новую пару.
        // Каждое устройство отзывается по порогу sid (Id refresh), а не по времени: у новой сессии Id строго больше,
        // а access, который CreateToken подписал уже после reset по прочитанному до него refresh, несёт старый sid.
        await SaveRevocations(userId, refreshTokens, deviceIds, cancellationToken);
        return deviceIds.Count(x => x != currentDeviceId);
    }

    private async Task SaveRevocations(long userId, List<RefreshToken> refreshTokens, IEnumerable<string> deviceIds,
        CancellationToken cancellationToken)
    {
        var revokedAt = DateTime.UtcNow;
        var expiresAt = revokedAt.AddMinutes(jwtSettings.ExpiryMinutes + 1);
        await context.RevokedSessions.Where(x => x.ExpiresAt <= revokedAt).ExecuteDeleteAsync(cancellationToken);

        // Устройство без refresh-строк (повторный logout) порога сессии не имеет — отзыв по времени.
        var maxSessionIds = refreshTokens.GroupBy(x => x.DeviceId).ToDictionary(x => x.Key, x => x.Max(t => t.Id));
        context.RefreshTokens.RemoveRange(refreshTokens);
        context.RevokedSessions.AddRange(deviceIds.Select(deviceId => new RevokedSession
        {
            UserId = userId,
            DeviceId = deviceId,
            RevokedAt = revokedAt,
            ExpiresAt = expiresAt,
            MaxSessionId = maxSessionIds.TryGetValue(deviceId, out var maxSessionId) ? maxSessionId : null
        }));

        // EF сохраняет удаление refresh и вставку отзывов в одной транзакции.
        await context.SaveChangesAsync(cancellationToken);
    }
}
