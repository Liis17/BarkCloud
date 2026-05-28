using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Users.Persistence.Services;

public class UsersStorage : IUsersStorage
{
    private readonly UsersContext _usersContext;

    public UsersStorage(UsersContext usersContext)
    {
        _usersContext = usersContext;
    }

    public async Task<User?> GetUserByUsername(string username)
    {
        var user = await _usersContext.Users.Include(u => u.Contact)
            .FirstOrDefaultAsync(x => string.Equals(x.Username.ToLower(), username.ToLower()));

        return user;
    }

    public async Task<User?> GetUserByEmail(string email)
    {
        var userContact = await _usersContext.UserContacts.Include(u => u.User)
            .FirstOrDefaultAsync(x => string.Equals(x.Email.ToLower(), email.ToLower()));

        return userContact?.User;
    }

    public async Task<User?> GetById(long id)
    {
        var user = await _usersContext.Users.Include(u => u.Contact).FirstOrDefaultAsync(x => x.Id == id);

        return user;
    }

    public async Task<List<User>> GetByIds(List<long> ids)
    {
        var users = await _usersContext.Users
            .Include(u => u.Contact)
            .Where(x => ids.Contains(x.Id))
            .ToListAsync();

        return users;
    }

    public async Task<User> CreateUser(string username, string firstName, string lastName, string email)
    {
        var contactUser = new UserContact { Email = email };

        var unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var user = new User
        {
            Id = unixTimestamp,
            Username = username,
            FirstName = firstName,
            LastName = lastName,
            RegistrationDate = DateTime.UtcNow,
            Contact = contactUser,
            IsDraft = true,
        };

        await _usersContext.Users.AddAsync(user);

        await _usersContext.SaveChangesAsync();

        return user;
    }

    public async Task ChangeDraftStatus(long userId, bool isDraft)
    {
        var user = await _usersContext.Users.FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            throw new UserNotFoundException();
        }

        user.IsDraft = isDraft;

        await _usersContext.SaveChangesAsync();
    }

    public async Task UpdateProfilePicture(long userId, string profilePictureUrl, string profilePicturePreviewUrl)
    {
        var user = await _usersContext.Users.FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            throw new UserNotFoundException();
        }

        user.ProfilePicture = profilePictureUrl;
        user.ProfilePicturePreviewUrl = profilePicturePreviewUrl;
        await _usersContext.SaveChangesAsync();
    }


    public async Task UpdateTrackedUser(User user)
    {
        _usersContext.Users.Update(user);

        await _usersContext.SaveChangesAsync();
    }

    public async Task ChangeName(long userId, string firstName, string lastName)
    {
        var user = await _usersContext.Users.FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            throw new UserNotFoundException();
        }

        user.FirstName = firstName;
        user.LastName = lastName;

        await _usersContext.SaveChangesAsync();
    }

    public async Task ChangeUsername(long userId, string username)
    {
        var user = await _usersContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        if (user is null)
        {
            throw new UserNotFoundException();
        }

        user.Username = username;

        await _usersContext.SaveChangesAsync();
    }

    public async Task ChangeBio(long userId, string? bio)
    {
        var user = await _usersContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        if (user is null)
        {
            throw new UserNotFoundException();
        }

        user.Bio = bio;

        await _usersContext.SaveChangesAsync();
    }

    public async Task<List<User>> SearchUsers(string query, long excludeUserId, int limit)
    {
        var pattern = query.Trim().ToLower();

        return await _usersContext.Users
            .Include(u => u.Contact)
            .Where(u => !u.IsDraft && u.Id != excludeUserId)
            .Where(u => u.Privacy == null || u.Privacy.SearchableByUsername)
            .Where(u =>
                u.Username.ToLower().Contains(pattern) ||
                u.FirstName.ToLower().Contains(pattern) ||
                u.LastName.ToLower().Contains(pattern))
            .OrderBy(u => u.Username)
            .Take(limit)
            .ToListAsync();
    }

    public async Task DeleteUser(long userId)
    {
        var user = await _usersContext.Users.FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            throw new UserNotFoundException();
        }

        // Связанные UserContact / UserDevice / UserPrivacy удалятся каскадно (см. UsersContext).
        _usersContext.Users.Remove(user);

        await _usersContext.SaveChangesAsync();
    }

    public async Task<UserPrivacy> GetOrCreatePrivacy(long userId)
    {
        var privacy = await _usersContext.UserPrivacies.FirstOrDefaultAsync(p => p.UserId == userId);

        if (privacy is not null)
        {
            return privacy;
        }

        var userExists = await _usersContext.Users.AnyAsync(u => u.Id == userId);
        if (!userExists)
        {
            throw new UserNotFoundException();
        }

        privacy = new UserPrivacy { UserId = userId };
        await _usersContext.UserPrivacies.AddAsync(privacy);
        await _usersContext.SaveChangesAsync();

        return privacy;
    }

    public async Task<UserPrivacy> UpdatePrivacy(long userId, PrivacyVisibility profileVisibility,
        PrivacyVisibility emailVisibility, PrivacyVisibility lastSeenVisibility, bool searchableByUsername)
    {
        var privacy = await GetOrCreatePrivacy(userId);

        privacy.ProfileVisibility = profileVisibility;
        privacy.EmailVisibility = emailVisibility;
        privacy.LastSeenVisibility = lastSeenVisibility;
        privacy.SearchableByUsername = searchableByUsername;

        await _usersContext.SaveChangesAsync();

        return privacy;
    }

    public async Task UpdateStorageLimitGb(long userId, int limitGb)
    {
        var user = await _usersContext.Users.FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
            throw new UserNotFoundException();

        user.StorageLimitGb = limitGb;

        await _usersContext.SaveChangesAsync();
    }
}
