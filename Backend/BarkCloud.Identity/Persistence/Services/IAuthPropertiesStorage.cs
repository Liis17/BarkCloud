using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IAuthPropertiesStorage
{
    Task<bool> CheckOtpEnabled(long userId);
    Task SetPendingOtpSecret(long userId, string secretKey, DateTime expiresAt, CancellationToken cancellationToken = default,
        long securityStateVersion = 0);
    Task<bool> ActivatePendingOtpSecret(long userId, string verifiedSecret, CancellationToken cancellationToken = default,
        long acceptedTotpStep = -1, long expectedSecurityStateVersion = 0);
    Task<bool> TryConsumeTotpStep(long userId, string secret, long generation, long timeStep,
        CancellationToken cancellationToken = default);
    Task<bool> TryReserveReauthPasswordAttempt(long userId, CancellationToken cancellationToken = default);
    Task ResetReauthPasswordAttempts(long userId, CancellationToken cancellationToken = default);
    Task<string?> GetOtpSecretKey(long userId, CancellationToken cancellationToken = default);
    Task EnableEmailOtp(long userId, CancellationToken cancellationToken = default);
    Task<AuthUserProperty?> GetUserAuthProperties(long userId, CancellationToken cancellationToken = default);
    Task DisableOtp(long userId, CancellationToken cancellationToken = default);
    Task DisableEmailOtp(long userId, CancellationToken cancellationToken = default);
    Task<bool> TryIssueEmailAuthCode(long userId, EmailAuthCodePurpose purpose, string code, CancellationToken cancellationToken = default);
    Task<EmailAuthCodeIssueResult> TryIssueEmailAuthCodeWithSnapshot(long userId, EmailAuthCodePurpose purpose,
        string code, CancellationToken cancellationToken = default, Guid? requestId = null);
    Task<ValidatedEmailAuthCode?> TryValidateAndReserveEmailAuthCode(long userId, EmailAuthCodePurpose purpose, string? code,
        CancellationToken cancellationToken = default, Guid? requestId = null);
    Task<bool> TryConsumeValidatedEmailAuthCode(ValidatedEmailAuthCode validatedCode, CancellationToken cancellationToken = default);
    Task UpdateOptType(OtpType type, long userId, CancellationToken cancellationToken = default);
    Task DeleteByUserId(long userId);
}
