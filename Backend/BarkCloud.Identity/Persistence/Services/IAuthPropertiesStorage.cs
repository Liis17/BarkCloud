using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IAuthPropertiesStorage
{
    Task<bool> CheckOtpEnabled(long userId);
    Task AddUserOtpSecretKey(long userId, string secretKey);
    Task<string?> GetOtpSecretKey(long userId);
    Task EnableOtp(long userId);
    Task EnableEmailOtp(long userId);
    Task<AuthUserProperty?> GetUserAuthProperties(long userId);
    Task DisableOtp(long userId);
    Task DisableEmailOtp(long userId);
    Task UpdateLastEmailAuthCode(long userId, string code);
    Task UpdateOptType(OtpType type, long userId);
    Task DeleteByUserId(long userId);
}
