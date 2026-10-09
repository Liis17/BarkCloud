using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IWebAuthnStorage
{
    // === Credentials ===
    Task AddCredential(WebAuthnCredential credential);
    Task<List<WebAuthnCredential>> GetCredentialsByUserId(long userId);
    Task<WebAuthnCredential?> GetCredentialByCredentialId(byte[] credentialId, CancellationToken cancellationToken = default);
    Task<bool> IsCredentialIdUnique(byte[] credentialId);
    Task<bool> TryUpdateCounter(long id, long expectedCounter, long verifiedCounter, CancellationToken cancellationToken = default);
    Task<bool> RemoveCredential(long userId, long id);

    // === User handle (на пользователя, в AuthUserProperty) ===
    Task<byte[]> GetOrCreateUserHandle(long userId);
    Task<long?> GetUserIdByUserHandle(byte[] userHandle, CancellationToken cancellationToken = default);

    // === Challenges ===
    Task SaveChallenge(WebAuthnChallenge challenge);
    Task<WebAuthnChallenge?> GetChallenge(Guid id, CancellationToken cancellationToken = default);
    Task<bool> TryConsumeAssertionChallenge(Guid id, DateTime expectedExpiresAt, CancellationToken cancellationToken = default);
    Task DeleteChallenge(Guid id);
}
