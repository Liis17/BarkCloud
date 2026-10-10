namespace BarkCloud.Identity.Persistence.Services;

public interface IPasswordsStorage
{
    Task<bool> UpdateUserPasswordHash(long userId, string passwordHash, CancellationToken cancellationToken = default);
    Task<string?> GetUserPasswordHash(long userId, CancellationToken cancellationToken = default);
    Task DeleteByUserId(long userId);
}
