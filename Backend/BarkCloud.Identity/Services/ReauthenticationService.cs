using System.Security.Cryptography;
using System.Text;

using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Services;

public sealed class ReauthenticationService(IdentityContext context, IPasswordsStorage passwordsStorage)
{
    public static string CreatePermissionToken(out byte[] hash)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        hash = HashToken(token);
        return token;
    }

    public static byte[] HashToken(string token)
        => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public Task<bool> IsSessionActive(long userId, long sessionId, CancellationToken cancellationToken = default)
        => context.RefreshTokens.AnyAsync(x => x.Id == sessionId && x.UserId == userId
                                                && x.ExpiresAt > DateTime.UtcNow,
            cancellationToken);

    public Task<long> GetSecurityStateVersion(long userId, CancellationToken cancellationToken = default)
        => passwordsStorage.GetSecurityStateVersion(userId, cancellationToken);

    public async Task<ReauthPermission?> FindGrantedAsync(string token, long userId, long sessionId,
        int action, long? credentialId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var hash = HashToken(token);
        var permission = await context.ReauthPermissions.FirstOrDefaultAsync(x =>
            x.PermissionTokenHash == hash && x.UserId == userId && x.SessionId == sessionId
            && x.Action == action && x.CredentialId == credentialId
            && x.Status == ReauthPermissionStatus.Granted && x.ExpiresAt > DateTime.UtcNow,
            cancellationToken);
        if (permission is null)
            return null;

        var version = await passwordsStorage.GetSecurityStateVersion(userId, cancellationToken);
        return permission.SecurityStateVersion == version
               && await IsSessionActive(userId, sessionId, cancellationToken)
            ? permission
            : null;
    }

    public async Task<bool> TryConsumeForRegistrationAsync(string token, long userId, long sessionId,
        int action, long securityStateVersion, Guid challengeId, CancellationToken cancellationToken = default)
    {
        var permission = await FindGrantedAsync(token, userId, sessionId, action, null, cancellationToken);
        if (permission is null || permission.SecurityStateVersion != securityStateVersion)
            return false;

        permission.Status = ReauthPermissionStatus.RegistrationPending;
        permission.WebAuthnChallengeId = challengeId;
        return await context.SaveChangesAsync(cancellationToken) > 0;
    }

    public async Task<bool> TryConsumeGrantedAsync(string token, long userId, long sessionId,
        int action, long? credentialId, CancellationToken cancellationToken = default)
    {
        var permission = await FindGrantedAsync(token, userId, sessionId, action, credentialId, cancellationToken);
        if (permission is null)
            return false;

        permission.Status = ReauthPermissionStatus.Consumed;
        return true;
    }

    public async Task<bool> TryCompleteRegistrationAsync(string token, Guid permissionId, Guid challengeId,
        long userId, long sessionId, long securityStateVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var hash = HashToken(token);
        var permission = await context.ReauthPermissions.FirstOrDefaultAsync(x =>
            x.Id == permissionId && x.WebAuthnChallengeId == challengeId
            && x.PermissionTokenHash == hash && x.UserId == userId && x.SessionId == sessionId
            && x.Action == (int)BarkCloud.Proto.Identity.ReauthenticationAction.AddPasskey
            && x.Status == ReauthPermissionStatus.RegistrationPending && x.ExpiresAt > DateTime.UtcNow,
            cancellationToken);
        if (permission is null || permission.SecurityStateVersion != securityStateVersion
            || await passwordsStorage.GetSecurityStateVersion(userId, cancellationToken) != securityStateVersion
            || !await IsSessionActive(userId, sessionId, cancellationToken))
            return false;

        permission.Status = ReauthPermissionStatus.Consumed;
        return true;
    }
}
