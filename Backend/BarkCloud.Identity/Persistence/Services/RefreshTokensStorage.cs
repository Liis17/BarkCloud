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

    public async Task<RefreshToken?> CreateNewRefreshToken(string refreshToken, long userId, string deviceId, int expiresDays)
    {
        var refreshTokenEntity = new RefreshToken()
        {
            CreatedAt = DateTime.UtcNow,
            DeviceId = deviceId,
            ExpiresAt = DateTime.UtcNow.AddDays(expiresDays),
            UserId = userId,
            Value = refreshToken
        };

        var token = await context.RefreshTokens.AddAsync(refreshTokenEntity);

        await context.SaveChangesAsync();

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
        var deviceIds = refreshTokens.Select(x => x.DeviceId)
            .Where(x => x != currentDeviceId).Distinct().ToList();

        // Все прежние refresh удаляются, включая текущий: сброс пароля затем выдаёт текущему устройству новую пару.
        // Отзыв по времени здесь не годится — iat в секундах, новый access попал бы под отзыв. Старые access
        // текущего устройства отзываются по порогу sid (Id refresh): у новой сессии Id строго больше.
        var currentMaxSessionId = refreshTokens.Where(x => x.DeviceId == currentDeviceId).Max(x => (long?)x.Id);
        await SaveRevocations(userId, refreshTokens, deviceIds, cancellationToken,
            currentMaxSessionId.HasValue ? (currentDeviceId!, currentMaxSessionId.Value) : null);
        return deviceIds.Count;
    }

    private async Task SaveRevocations(long userId, List<RefreshToken> refreshTokens, IEnumerable<string> deviceIds,
        CancellationToken cancellationToken, (string DeviceId, long MaxSessionId)? replacedSession = null)
    {
        var revokedAt = DateTime.UtcNow;
        var expiresAt = revokedAt.AddMinutes(jwtSettings.ExpiryMinutes + 1);
        await context.RevokedSessions.Where(x => x.ExpiresAt <= revokedAt).ExecuteDeleteAsync(cancellationToken);

        context.RefreshTokens.RemoveRange(refreshTokens);
        context.RevokedSessions.AddRange(deviceIds.Select(deviceId => new RevokedSession
        {
            UserId = userId,
            DeviceId = deviceId,
            RevokedAt = revokedAt,
            ExpiresAt = expiresAt
        }));
        if (replacedSession.HasValue)
        {
            context.RevokedSessions.Add(new RevokedSession
            {
                UserId = userId,
                DeviceId = replacedSession.Value.DeviceId,
                RevokedAt = revokedAt,
                ExpiresAt = expiresAt,
                MaxSessionId = replacedSession.Value.MaxSessionId
            });
        }

        // EF сохраняет удаление refresh и вставку отзывов в одной транзакции.
        await context.SaveChangesAsync(cancellationToken);
    }
}
