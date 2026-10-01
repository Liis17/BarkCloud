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

    public async Task DeleteRefreshTokensByDeviceId(string deviceId, long userId)
    {
        var refreshTokens = await context.RefreshTokens
            .Where(x => x.DeviceId == deviceId && x.UserId == userId)
            .ToListAsync();

        if (refreshTokens.Count == 0)
        {
            throw new RefreshTokenNotFoundException();
        }

        context.RefreshTokens.RemoveRange(refreshTokens);

        await context.SaveChangesAsync();
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

    /// <summary>
    /// Удаляет все refresh-токены пользователя (при удалении аккаунта).
    /// Возвращает список уникальных DeviceId для последующего отзыва access-токенов.
    /// </summary>
    public async Task<List<string>> DeleteAllByUserId(long userId)
    {
        var refreshTokens = await context.RefreshTokens
            .Where(x => x.UserId == userId)
            .ToListAsync();

        var deviceIds = refreshTokens.Select(x => x.DeviceId).Distinct().ToList();

        if (refreshTokens.Count > 0)
        {
            context.RefreshTokens.RemoveRange(refreshTokens);
            await context.SaveChangesAsync();
        }

        return deviceIds;
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

    public async Task<int> RevokeAllSessions(long userId, string? exceptDeviceId = null, CancellationToken cancellationToken = default)
    {
        var refreshTokens = await context.RefreshTokens
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);
        var deviceIds = refreshTokens.Select(x => x.DeviceId)
            .Where(x => x != exceptDeviceId).Distinct().ToList();

        // Исключение касается access-токена текущего устройства. Все прежние refresh
        // удаляются, включая текущий: сброс пароля затем выдаёт новую пару токенов.
        await SaveRevocations(userId, refreshTokens, deviceIds, cancellationToken);
        return deviceIds.Count;
    }

    private async Task SaveRevocations(long userId, List<RefreshToken> refreshTokens, IEnumerable<string> deviceIds,
        CancellationToken cancellationToken)
    {
        var revokedAt = DateTime.UtcNow;
        await context.RevokedSessions.Where(x => x.ExpiresAt <= revokedAt).ExecuteDeleteAsync(cancellationToken);

        context.RefreshTokens.RemoveRange(refreshTokens);
        context.RevokedSessions.AddRange(deviceIds.Select(deviceId => new RevokedSession
        {
            UserId = userId,
            DeviceId = deviceId,
            RevokedAt = revokedAt,
            ExpiresAt = revokedAt.AddMinutes(jwtSettings.ExpiryMinutes + 1)
        }));

        // EF сохраняет удаление refresh и вставку отзывов в одной транзакции.
        await context.SaveChangesAsync(cancellationToken);
    }
}
