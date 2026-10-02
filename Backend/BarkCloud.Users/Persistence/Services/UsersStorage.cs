using BarkCloud.Shared.Exceptions;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Users.Domain;
using BarkCloud.Users.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

using Npgsql;

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

        var user = new User
        {
            Username = username,
            FirstName = firstName,
            LastName = lastName,
            RegistrationDate = DateTime.UtcNow,
            Contact = contactUser,
            IsDraft = true,
        };

        await _usersContext.Users.AddAsync(user);

        try
        {
            await _usersContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (GetLoginConflict(ex) is { } conflict)
        {
            _usersContext.Entry(contactUser).State = EntityState.Detached;
            _usersContext.Entry(user).State = EntityState.Detached;
            throw conflict;
        }

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


    public async Task OverrideDraftUser(long userId, string username, string firstName, string lastName, string email)
    {
        await using var transaction = await _usersContext.Database.BeginTransactionAsync();
        try
        {
            var updated = await _usersContext.Users
                .Where(u => u.Id == userId && u.IsDraft && u.Contact.Email.ToLower() == email.ToLower())
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.Username, username)
                    .SetProperty(u => u.FirstName, firstName)
                    .SetProperty(u => u.LastName, lastName)
                    .SetProperty(u => u.ProfilePicture, (string?)null)
                    .SetProperty(u => u.RegistrationDate, DateTime.UtcNow));

            if (updated == 0)
                throw new EmailExistException();

            await _usersContext.UserContacts.Where(c => c.UserId == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.Email, email));

            await transaction.CommitAsync();
        }
        catch (Exception ex) when (GetLoginConflict(ex) is { } conflict)
        {
            throw conflict;
        }

        foreach (var entry in _usersContext.ChangeTracker.Entries<UserContact>().Where(e => e.Entity.UserId == userId).ToList())
            entry.State = EntityState.Detached;
        foreach (var entry in _usersContext.ChangeTracker.Entries<User>().Where(e => e.Entity.Id == userId).ToList())
            entry.State = EntityState.Detached;
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

        var owner = await GetUserByUsername(username);
        if (owner is not null && owner.Id != userId)
        {
            throw new UsernameExistException();
        }

        var previousUsername = user.Username;
        user.Username = username;

        try
        {
            await _usersContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (GetLoginConflict(ex) is { } conflict)
        {
            user.Username = previousUsername;
            throw conflict;
        }
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

    public async Task DeleteUser(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _usersContext.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);

        if (user is null)
        {
            throw new UserNotFoundException();
        }

        // Связанные UserContact / UserDevice / UserPrivacy удалятся каскадно (см. UsersContext).
        _usersContext.Users.Remove(user);

        await _usersContext.SaveChangesAsync(cancellationToken);
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

    private static BaseGrpcException? GetLoginConflict(Exception exception)
    {
        var postgresException = exception switch
        {
            PostgresException postgres => postgres,
            DbUpdateException { InnerException: PostgresException postgres } => postgres,
            _ => null,
        };

        if (postgresException?.SqlState != PostgresErrorCodes.UniqueViolation)
            return null;

        return postgresException.ConstraintName switch
        {
            "UX_Users_Username_Lower" => new UsernameExistException(),
            "UX_UserContacts_Email_Lower" => new EmailExistException(),
            _ => null,
        };
    }
}
