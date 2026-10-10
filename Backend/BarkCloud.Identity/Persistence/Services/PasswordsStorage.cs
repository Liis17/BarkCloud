using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Services;

public class PasswordsStorage : IPasswordsStorage
{
    private readonly IdentityContext _context;

    public PasswordsStorage(IdentityContext context)
    {
        _context = context;
    }

    public async Task<bool> UpdateUserPasswordHash(long userId, string passwordHash, CancellationToken cancellationToken = default)
    {
        var userPassword = await _context.UserPasswords.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        if (userPassword is null)
        {
            userPassword = new UserPassword { UserId = userId, ChangedAt = DateTime.UtcNow, PasswordHash = passwordHash };

            _context.UserPasswords.Add(userPassword);

            await _context.SaveChangesAsync(cancellationToken);

            return true;
        }

        userPassword.ChangedAt = DateTime.UtcNow;
        userPassword.PasswordHash = passwordHash;

        _context.UserPasswords.Update(userPassword);

        await _context.SaveChangesAsync(cancellationToken);

        return false;
    }

    public async Task<string?> GetUserPasswordHash(long userId, CancellationToken cancellationToken = default)
    {
        var userPassword = await _context.UserPasswords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        return userPassword?.PasswordHash;
    }

    /// <summary>
    /// Удаляет пароль пользователя (при удалении аккаунта).
    /// </summary>
    public async Task DeleteByUserId(long userId)
    {
        await _context.UserPasswords.Where(x => x.UserId == userId).ExecuteDeleteAsync();
    }
}
