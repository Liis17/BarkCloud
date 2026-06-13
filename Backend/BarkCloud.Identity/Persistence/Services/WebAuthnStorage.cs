using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Services;

public class WebAuthnStorage : IWebAuthnStorage
{
    private readonly IdentityContext _context;

    public WebAuthnStorage(IdentityContext context)
    {
        _context = context;
    }

    public async Task AddCredential(WebAuthnCredential credential)
    {
        await _context.WebAuthnCredentials.AddAsync(credential);
        await _context.SaveChangesAsync();
    }

    public Task<List<WebAuthnCredential>> GetCredentialsByUserId(long userId)
        => _context.WebAuthnCredentials.Where(x => x.UserId == userId).ToListAsync();

    public Task<WebAuthnCredential?> GetCredentialByCredentialId(byte[] credentialId)
        => _context.WebAuthnCredentials.FirstOrDefaultAsync(x => x.CredentialId == credentialId);

    public async Task<bool> IsCredentialIdUnique(byte[] credentialId)
        => !await _context.WebAuthnCredentials.AnyAsync(x => x.CredentialId == credentialId);

    public async Task UpdateCounter(long id, long counter)
    {
        var credential = await _context.WebAuthnCredentials.FirstOrDefaultAsync(x => x.Id == id);

        if (credential is null)
        {
            return;
        }

        credential.SignatureCounter = counter;
        credential.LastUsedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
    }

    public async Task<bool> RemoveCredential(long userId, long id)
    {
        var deleted = await _context.WebAuthnCredentials
            .Where(x => x.UserId == userId && x.Id == id)
            .ExecuteDeleteAsync();

        return deleted > 0;
    }

    public async Task<byte[]> GetOrCreateUserHandle(long userId)
    {
        var props = await _context.AuthUserProperties.FirstOrDefaultAsync(x => x.UserId == userId);

        if (props is null)
        {
            props = new AuthUserProperty
            {
                UserId = userId,
                WebAuthnUserHandle = Guid.NewGuid().ToByteArray()
            };

            await _context.AuthUserProperties.AddAsync(props);
            await _context.SaveChangesAsync();

            return props.WebAuthnUserHandle!;
        }

        if (props.WebAuthnUserHandle is null || props.WebAuthnUserHandle.Length == 0)
        {
            props.WebAuthnUserHandle = Guid.NewGuid().ToByteArray();
            await _context.SaveChangesAsync();
        }

        return props.WebAuthnUserHandle!;
    }

    public async Task<long?> GetUserIdByUserHandle(byte[] userHandle)
    {
        var props = await _context.AuthUserProperties
            .FirstOrDefaultAsync(x => x.WebAuthnUserHandle == userHandle);

        return props?.UserId;
    }

    public async Task SaveChallenge(WebAuthnChallenge challenge)
    {
        await _context.WebAuthnChallenges.AddAsync(challenge);
        await _context.SaveChangesAsync();
    }

    public Task<WebAuthnChallenge?> GetChallenge(Guid id)
        => _context.WebAuthnChallenges.FirstOrDefaultAsync(x => x.Id == id);

    public async Task DeleteChallenge(Guid id)
    {
        await _context.WebAuthnChallenges.Where(x => x.Id == id).ExecuteDeleteAsync();
    }
}
