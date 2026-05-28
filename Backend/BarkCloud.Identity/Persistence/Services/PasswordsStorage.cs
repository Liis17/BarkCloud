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

    public async Task<bool> UpdateUserPasswordHash(long userId, string passwordHash)
    {
        var userPassword = await _context.UserPasswords.FirstOrDefaultAsync(x => x.UserId == userId);

        if (userPassword is null)
        {
            userPassword = new UserPassword { UserId = userId, ChangedAt = DateTime.UtcNow, PasswordHash = passwordHash };

            _context.UserPasswords.Add(userPassword);

            await _context.SaveChangesAsync();

            return true;
        }

        userPassword.ChangedAt = DateTime.UtcNow;
        userPassword.PasswordHash = passwordHash;

        _context.UserPasswords.Update(userPassword);

        await _context.SaveChangesAsync();

        return false;
    }

    public async Task<string?> GetUserPasswordHash(long userId)
    {
        var userPassword = await _context.UserPasswords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId);

        return userPassword?.PasswordHash;
    }

    /// <summary>
    /// Очищает хеш пароля пользователя, позволяя установить новый пароль без ввода старого.
    /// Используется при сбросе пароля после успешной проверки OTP кода.
    /// </summary>
    /// <param name="userId">Идентификатор пользователя</param>
    public async Task ClearUserPasswordHash(long userId)
    {
        var userPassword = await _context.UserPasswords.FirstOrDefaultAsync(x => x.UserId == userId);

        if (userPassword is not null)
        {
            userPassword.PasswordHash = null;
            userPassword.ChangedAt = DateTime.UtcNow;

            _context.UserPasswords.Update(userPassword);
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Удаляет пароль пользователя (при удалении аккаунта).
    /// </summary>
    public async Task DeleteByUserId(long userId)
    {
        await _context.UserPasswords.Where(x => x.UserId == userId).ExecuteDeleteAsync();
    }
}