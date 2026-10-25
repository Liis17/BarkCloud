using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Services;

public class PasswordsStorage : IPasswordsStorage
{
    private readonly IdentityContext _context;

    public PasswordsStorage(IdentityContext context)
    {
        _context = context;
    }

    public async Task<bool> UpdateUserPasswordHash(long userId, string passwordHash, CancellationToken cancellationToken = default)
    {
        var userPassword = await _context.UserPasswords.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        if (userPassword is null)
        {
            userPassword = new UserPassword { UserId = userId, ChangedAt = DateTime.UtcNow, PasswordHash = passwordHash, SecurityStateVersion = 1 };

            _context.UserPasswords.Add(userPassword);

            await _context.SaveChangesAsync(cancellationToken);
            await InvalidatePendingSecurityOperations(userId, cancellationToken);

            return true;
        }

        userPassword.ChangedAt = DateTime.UtcNow;
        userPassword.PasswordHash = passwordHash;
        userPassword.SecurityStateVersion++;

        _context.UserPasswords.Update(userPassword);

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePendingSecurityOperations(userId, cancellationToken);

        return false;
    }

    public async Task<bool> CreateInitialPassword(long userId, string passwordHash,
        CancellationToken cancellationToken = default)
    {
        var userPassword = await _context.UserPasswords.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (userPassword is not null && !string.IsNullOrEmpty(userPassword.PasswordHash))
            return false;

        if (userPassword is null)
        {
            userPassword = new UserPassword { UserId = userId };
            _context.UserPasswords.Add(userPassword);
        }

        userPassword.ChangedAt = DateTime.UtcNow;
        userPassword.PasswordHash = passwordHash;
        userPassword.SecurityStateVersion++;
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePendingSecurityOperations(userId, cancellationToken);
        return true;
    }

    public async Task<string?> GetUserPasswordHash(long userId, CancellationToken cancellationToken = default)
    {
        var userPassword = await _context.UserPasswords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        return userPassword?.PasswordHash;
    }

    public async Task<long> GetSecurityStateVersion(long userId, CancellationToken cancellationToken = default)
        => await _context.UserPasswords.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.SecurityStateVersion)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task InvalidatePendingSecurityOperations(long userId, CancellationToken cancellationToken)
    {
        await _context.ReauthPermissions.Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.WebAuthnChallenges.Where(x => x.UserId == userId
                && (x.Type == WebAuthnChallengeType.Registration
                    || x.Type == WebAuthnChallengeType.ReauthenticationAssertion))
            .ExecuteDeleteAsync(cancellationToken);
        await _context.AuthUserProperties.Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PendingOtpSecret, (string?)null)
                .SetProperty(x => x.PendingOtpSecretExpiresAt, (DateTime?)null)
                .SetProperty(x => x.PendingOtpSecurityStateVersion, 0)
                .SetProperty(x => x.SelectedOtpType, x => x.OtpEnabled
                    ? OtpType.Authenticator
                    : x.EmailOtpEnabled ? OtpType.Email : OtpType.Unknown), cancellationToken);
        await _context.AuthUserProperties.Where(x => x.UserId == userId
                && (x.EmailAuthCodePurpose == EmailAuthCodePurpose.Reauthentication
                    || x.EmailAuthCodePurpose == EmailAuthCodePurpose.EnableEmailOtp
                    || x.EmailAuthCodePurpose == EmailAuthCodePurpose.EnableAuthenticator
                    || x.EmailAuthCodePurpose == EmailAuthCodePurpose.DisableEmailOtp))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastEmailAuthCode, (string?)null)
                .SetProperty(x => x.EmailAuthCodePurpose, (EmailAuthCodePurpose?)null)
                .SetProperty(x => x.EmailAuthCodeRequestId, (Guid?)null)
                .SetProperty(x => x.EmailAuthCodeIssuedAt, (DateTime?)null)
                .SetProperty(x => x.EmailAuthCodeExpiresAt, (DateTime?)null)
                .SetProperty(x => x.EmailAuthCodeAttempts, 0), cancellationToken);
    }

    /// <summary>
    /// Удаляет пароль пользователя (при удалении аккаунта).
    /// </summary>
    public async Task DeleteByUserId(long userId)
    {
        await _context.UserPasswords.Where(x => x.UserId == userId).ExecuteDeleteAsync();
    }
}
