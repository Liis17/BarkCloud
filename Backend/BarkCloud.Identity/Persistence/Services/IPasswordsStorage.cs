namespace BarkCloud.Identity.Persistence.Services;

public interface IPasswordsStorage
{
    Task<bool> UpdateUserPasswordHash(long userId, string passwordHash);
    Task<string?> GetUserPasswordHash(long userId);
    Task ClearUserPasswordHash(long userId);
    Task DeleteByUserId(long userId);
}
