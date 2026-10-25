using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Persistence;

public sealed class PasswordsStorageSecurityStateTests : IDisposable
{
    private readonly SqliteIdentityContext _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task UpdatePassword_IncrementsVersionAndInvalidatesPendingSecurityOperations()
    {
        var setup = _database.Context;
        setup.UserPasswords.Add(new UserPassword
        {
            UserId = 7, PasswordHash = "old", SecurityStateVersion = 4
        });
        setup.AuthUserProperties.Add(new AuthUserProperty
        {
            UserId = 7,
            EmailOtpEnabled = true,
            SelectedOtpType = OtpType.Authenticator,
            PendingOtpSecret = "pending-secret",
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5),
            PendingOtpSecurityStateVersion = 4,
            LastEmailAuthCode = "123456",
            EmailAuthCodePurpose = EmailAuthCodePurpose.EnableEmailOtp,
            EmailAuthCodeRequestId = Guid.NewGuid(),
            EmailAuthCodeIssuedAt = DateTime.UtcNow,
            EmailAuthCodeExpiresAt = DateTime.UtcNow.AddMinutes(5),
            EmailAuthCodeAttempts = 2
        });
        setup.ReauthPermissions.Add(new ReauthPermission
        {
            Id = Guid.NewGuid(), UserId = 7, SessionId = 21, Action = 1,
            SecurityStateVersion = 4, Status = ReauthPermissionStatus.Granted,
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        setup.WebAuthnChallenges.AddRange(
            new WebAuthnChallenge
            {
                Id = Guid.NewGuid(), UserId = 7, Type = WebAuthnChallengeType.Registration,
                OptionsJson = "{}", ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            },
            new WebAuthnChallenge
            {
                Id = Guid.NewGuid(), UserId = 7, Type = WebAuthnChallengeType.ReauthenticationAssertion,
                OptionsJson = "{}", ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            },
            new WebAuthnChallenge
            {
                Id = Guid.NewGuid(), UserId = 7, Type = WebAuthnChallengeType.Assertion,
                OptionsJson = "{}", ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
        await setup.SaveChangesAsync();

        await new PasswordsStorage(setup).UpdateUserPasswordHash(7, "new");

        using var reader = _database.CreateAdditionalContext();
        (await reader.UserPasswords.AsNoTracking().SingleAsync(x => x.UserId == 7))
            .SecurityStateVersion.Should().Be(5);
        (await reader.ReauthPermissions.AnyAsync(x => x.UserId == 7)).Should().BeFalse();
        (await reader.WebAuthnChallenges.AsNoTracking().Where(x => x.UserId == 7)
                .Select(x => x.Type).ToListAsync())
            .Should().Equal(WebAuthnChallengeType.Assertion);
        var auth = await reader.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 7);
        auth.PendingOtpSecret.Should().BeNull();
        auth.PendingOtpSecretExpiresAt.Should().BeNull();
        auth.PendingOtpSecurityStateVersion.Should().Be(0);
        auth.SelectedOtpType.Should().Be(OtpType.Email);
        auth.LastEmailAuthCode.Should().BeNull();
        auth.EmailAuthCodePurpose.Should().BeNull();
        auth.EmailAuthCodeRequestId.Should().BeNull();
        auth.EmailAuthCodeIssuedAt.Should().BeNull();
        auth.EmailAuthCodeExpiresAt.Should().BeNull();
        auth.EmailAuthCodeAttempts.Should().Be(0);
    }
}
