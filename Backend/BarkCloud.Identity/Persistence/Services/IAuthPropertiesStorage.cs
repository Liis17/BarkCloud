using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IAuthPropertiesStorage
{
    Task<bool> CheckOtpEnabled(long userId);
    Task SetPendingOtpSecret(long userId, string secretKey, DateTime expiresAt);
    Task<bool> ActivatePendingOtpSecret(long userId, string verifiedSecret);
    Task<string?> GetOtpSecretKey(long userId);
    Task EnableEmailOtp(long userId);
    Task<AuthUserProperty?> GetUserAuthProperties(long userId);
    Task DisableOtp(long userId);
    Task DisableEmailOtp(long userId);
    Task<bool> TryIssueEmailAuthCode(long userId, EmailAuthCodePurpose purpose, string code);
    Task<bool> TryConsumeEmailAuthCode(long userId, EmailAuthCodePurpose purpose, string? code);
    Task UpdateOptType(OtpType type, long userId);
    Task DeleteByUserId(long userId);
}
