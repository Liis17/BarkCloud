using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IRefreshTokensStorage
{
    Task<RefreshToken?> FindRefreshToken(string refreshToken);
    Task<RefreshToken?> CreateNewRefreshToken(string refreshToken, long userId, string deviceId, int expiresDays);
    Task<List<RefreshToken>> GetRefreshTokens(long userId);
    Task DeleteRefreshTokensByDeviceId(string deviceId, long userId);
    Task DeleteRefreshTokensByDeviceIdSafe(string deviceId, long userId);
    Task<List<string>> DeleteAllByUserId(long userId);
    Task RevokeSession(string deviceId, long userId, CancellationToken cancellationToken = default);
    Task RevokeSessionSafe(string deviceId, long userId, CancellationToken cancellationToken = default);
    Task<int> RevokeAllSessions(long userId, string? exceptDeviceId = null, CancellationToken cancellationToken = default);
}
