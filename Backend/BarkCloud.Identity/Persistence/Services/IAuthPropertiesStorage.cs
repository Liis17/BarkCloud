using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IAuthPropertiesStorage
{
    Task<bool> CheckOtpEnabled(long userId);
    Task SetPendingOtpSecret(long userId, string secretKey, DateTime expiresAt, CancellationToken cancellationToken = default);
    Task<bool> ActivatePendingOtpSecret(long userId, string verifiedSecret, CancellationToken cancellationToken = default);
    Task<bool> TryReserveReauthPasswordAttempt(long userId, CancellationToken cancellationToken = default);
    Task ResetReauthPasswordAttempts(long userId, CancellationToken cancellationToken = default);
    Task<string?> GetOtpSecretKey(long userId, CancellationToken cancellationToken = default);
    Task EnableEmailOtp(long userId, CancellationToken cancellationToken = default);
    Task<AuthUserProperty?> GetUserAuthProperties(long userId, CancellationToken cancellationToken = default);
    Task DisableOtp(long userId, CancellationToken cancellationToken = default);
    Task DisableEmailOtp(long userId, CancellationToken cancellationToken = default);
    Task<bool> TryIssueEmailAuthCode(long userId, EmailAuthCodePurpose purpose, string code, CancellationToken cancellationToken = default);
    Task<ValidatedEmailAuthCode?> TryValidateAndReserveEmailAuthCode(long userId, EmailAuthCodePurpose purpose, string? code,
        CancellationToken cancellationToken = default);
    Task<bool> TryConsumeValidatedEmailAuthCode(ValidatedEmailAuthCode validatedCode, CancellationToken cancellationToken = default);
    Task UpdateOptType(OtpType type, long userId, CancellationToken cancellationToken = default);
    Task DeleteByUserId(long userId);
}
