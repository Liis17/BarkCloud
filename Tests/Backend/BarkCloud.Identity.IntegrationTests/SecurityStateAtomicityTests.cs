using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.IntegrationTests;

public sealed class SecurityStateAtomicityTests
{
    private const long UserId = 42;

    [Fact]
    public async Task PasswordChange_InvalidatesPendingTwoFactorAndPasskeyOperations()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        var registrationChallengeId = Guid.NewGuid();
        var reauthChallengeId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        await using (var setup = database.CreateContext())
        {
            setup.UserPasswords.Add(new UserPassword
            {
                UserId = UserId, PasswordHash = "old", SecurityStateVersion = 2
            });
            setup.AuthUserProperties.Add(new AuthUserProperty
            {
                UserId = UserId,
                PendingOtpSecret = "pending-secret",
                PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5),
                PendingOtpSecurityStateVersion = 2,
                SelectedOtpType = OtpType.Authenticator,
                LastEmailAuthCode = "123456",
                EmailAuthCodePurpose = EmailAuthCodePurpose.EnableAuthenticator,
                EmailAuthCodeRequestId = Guid.NewGuid(),
                EmailAuthCodeIssuedAt = DateTime.UtcNow,
                EmailAuthCodeExpiresAt = DateTime.UtcNow.AddMinutes(5),
                EmailAuthCodeAttempts = 1
            });
            setup.ReauthPermissions.Add(new ReauthPermission
            {
                Id = permissionId, UserId = UserId, SessionId = 7,
                Action = 1, SecurityStateVersion = 2,
                Status = ReauthPermissionStatus.RegistrationPending,
                WebAuthnChallengeId = registrationChallengeId,
                CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
            setup.WebAuthnChallenges.AddRange(
                new WebAuthnChallenge
                {
                    Id = registrationChallengeId, UserId = UserId,
                    Type = WebAuthnChallengeType.Registration, OptionsJson = "{}",
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5), SessionId = 7,
                    SecurityStateVersion = 2, ReauthPermissionId = permissionId
                },
                new WebAuthnChallenge
                {
                    Id = reauthChallengeId, UserId = UserId,
                    Type = WebAuthnChallengeType.ReauthenticationAssertion, OptionsJson = "{}",
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5), SessionId = 7,
                    SecurityStateVersion = 2
                },
                new WebAuthnChallenge
                {
                    Id = Guid.NewGuid(), UserId = UserId,
                    Type = WebAuthnChallengeType.Assertion, OptionsJson = "{}",
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                });
            await setup.SaveChangesAsync();
        }

        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await IdentityOwnerLock.AcquireAsync(context, UserId);
            await new PasswordsStorage(context).UpdateUserPasswordHash(UserId, "new");
            await transaction.CommitAsync();
        }

        await using var reader = database.CreateContext();
        (await reader.UserPasswords.AsNoTracking().SingleAsync(x => x.UserId == UserId))
            .SecurityStateVersion.Should().Be(3);
        (await reader.ReauthPermissions.AnyAsync(x => x.UserId == UserId)).Should().BeFalse();
        (await reader.WebAuthnChallenges.AsNoTracking().Where(x => x.UserId == UserId)
                .Select(x => x.Type).ToListAsync())
            .Should().Equal(WebAuthnChallengeType.Assertion);
        var auth = await reader.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == UserId);
        auth.PendingOtpSecret.Should().BeNull();
        auth.PendingOtpSecretExpiresAt.Should().BeNull();
        auth.EmailAuthCodePurpose.Should().BeNull();
        auth.LastEmailAuthCode.Should().BeNull();
        auth.SelectedOtpType.Should().Be(OtpType.Unknown);
    }
}
