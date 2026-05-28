using BarkCloud.Users.Domain;

namespace BarkCloud.Users.Persistence.Services;

public interface IUsersStorage
{
    Task<User?> GetUserByUsername(string username);
    Task<User?> GetUserByEmail(string email);
    Task<User?> GetById(long id);
    Task<List<User>> GetByIds(List<long> ids);
    Task<User> CreateUser(string username, string firstName, string lastName, string email);
    Task ChangeDraftStatus(long userId, bool isDraft);
    Task UpdateProfilePicture(long userId, string profilePictureUrl, string profilePicturePreviewUrl);
    Task UpdateTrackedUser(User user);
    Task ChangeName(long userId, string firstName, string lastName);
    Task ChangeUsername(long userId, string username);
    Task ChangeBio(long userId, string? bio);
    Task<List<User>> SearchUsers(string query, long excludeUserId, int limit);
    Task DeleteUser(long userId);
    Task<UserPrivacy> GetOrCreatePrivacy(long userId);
    Task<UserPrivacy> UpdatePrivacy(long userId, PrivacyVisibility profileVisibility,
        PrivacyVisibility emailVisibility, PrivacyVisibility lastSeenVisibility, bool searchableByUsername);
    Task UpdateStorageLimitGb(long userId, int limitGb);
}
