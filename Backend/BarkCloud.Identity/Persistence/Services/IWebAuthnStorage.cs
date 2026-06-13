using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IWebAuthnStorage
{
    // === Credentials ===
    Task AddCredential(WebAuthnCredential credential);
    Task<List<WebAuthnCredential>> GetCredentialsByUserId(long userId);
    Task<WebAuthnCredential?> GetCredentialByCredentialId(byte[] credentialId);
    Task<bool> IsCredentialIdUnique(byte[] credentialId);
    Task UpdateCounter(long id, long counter);
    Task<bool> RemoveCredential(long userId, long id);

    // === User handle (на пользователя, в AuthUserProperty) ===
    Task<byte[]> GetOrCreateUserHandle(long userId);
    Task<long?> GetUserIdByUserHandle(byte[] userHandle);

    // === Challenges ===
    Task SaveChallenge(WebAuthnChallenge challenge);
    Task<WebAuthnChallenge?> GetChallenge(Guid id);
    Task DeleteChallenge(Guid id);
}
