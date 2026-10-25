using System.Text;

using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Services;

public sealed class ReauthenticationServiceTests : IDisposable
{
    private const long UserId = 7;
    private const long SessionId = 55;
    private const long CredentialId = 81;
    private const string Token = "opaque-random-permission-token";
    private readonly SqliteIdentityContext _db = new();

    public void Dispose() => _db.Dispose();

    private async Task SeedPermission(DateTime? expiresAt = null, long securityVersion = 3)
    {
        _db.Context.UserPasswords.Add(new UserPassword
        {
            UserId = UserId,
            PasswordHash = "hash",
            SecurityStateVersion = securityVersion
        });
        _db.Context.RefreshTokens.Add(new RefreshToken
        {
            Id = SessionId,
            UserId = UserId,
            Value = "refresh-token",
            DeviceId = "device-1",
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        _db.Context.ReauthPermissions.Add(new ReauthPermission
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            SessionId = SessionId,
            Action = (int)ReauthenticationAction.RemovePasskey,
            CredentialId = CredentialId,
            SecurityStateVersion = securityVersion,
            PermissionTokenHash = ReauthenticationService.HashToken(Token),
            Status = ReauthPermissionStatus.Granted,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5)
        });
        await _db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task Permission_StoresOnlyHashAndBindsUserSessionActionAndCredential()
    {
        await SeedPermission();
        var service = new ReauthenticationService(_db.Context, new PasswordsStorage(_db.Context));
        var permission = await _db.Context.ReauthPermissions.SingleAsync();

        permission.PermissionTokenHash.Should().HaveCount(32);
        permission.PermissionTokenHash.Should().NotEqual(Encoding.UTF8.GetBytes(Token));
        (await service.FindGrantedAsync(Token, UserId, SessionId, (int)ReauthenticationAction.RemovePasskey,
            CredentialId)).Should().NotBeNull();
        (await service.FindGrantedAsync(Token, UserId + 1, SessionId, (int)ReauthenticationAction.RemovePasskey,
            CredentialId)).Should().BeNull();
        (await service.FindGrantedAsync(Token, UserId, SessionId + 1, (int)ReauthenticationAction.RemovePasskey,
            CredentialId)).Should().BeNull();
        (await service.FindGrantedAsync(Token, UserId, SessionId, (int)ReauthenticationAction.AddPasskey,
            CredentialId)).Should().BeNull();
        (await service.FindGrantedAsync(Token, UserId, SessionId, (int)ReauthenticationAction.RemovePasskey,
            CredentialId + 1)).Should().BeNull();
    }

    [Fact]
    public async Task Permission_MissingIsRejected()
    {
        var service = new ReauthenticationService(_db.Context, new PasswordsStorage(_db.Context));

        (await service.TryConsumeGrantedAsync(Token, UserId, SessionId,
            (int)ReauthenticationAction.RemovePasskey, CredentialId)).Should().BeFalse();
    }

    [Fact]
    public async Task Permission_ExpiresAndCannotBeUsedAfterPasswordChanges()
    {
        await SeedPermission(DateTime.UtcNow.AddSeconds(-1));
        var service = new ReauthenticationService(_db.Context, new PasswordsStorage(_db.Context));

        (await service.FindGrantedAsync(Token, UserId, SessionId, (int)ReauthenticationAction.RemovePasskey,
            CredentialId)).Should().BeNull();

        await _db.Context.ReauthPermissions.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(5)));
        await _db.Context.UserPasswords.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.SecurityStateVersion, x => x.SecurityStateVersion + 1));

        (await service.FindGrantedAsync(Token, UserId, SessionId, (int)ReauthenticationAction.RemovePasskey,
            CredentialId)).Should().BeNull();
    }

    [Fact]
    public async Task Permission_IsConsumedOnceInsideTheCallerTransaction()
    {
        await SeedPermission();
        var service = new ReauthenticationService(_db.Context, new PasswordsStorage(_db.Context));

        await using (var transaction = await _db.Context.Database.BeginTransactionAsync())
        {
            (await service.TryConsumeGrantedAsync(Token, UserId, SessionId,
                (int)ReauthenticationAction.RemovePasskey, CredentialId)).Should().BeTrue();
            await _db.Context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        (await service.TryConsumeGrantedAsync(Token, UserId, SessionId,
            (int)ReauthenticationAction.RemovePasskey, CredentialId)).Should().BeFalse();
    }
}
