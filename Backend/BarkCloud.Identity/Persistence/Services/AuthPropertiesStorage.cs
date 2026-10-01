using System.Security.Cryptography;
using System.Text;

using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Exceptions;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Services;

public class AuthPropertiesStorage : IAuthPropertiesStorage
{
    private const int EmailAuthCodeMaxAttempts = 5;
    private static readonly TimeSpan EmailAuthCodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EmailAuthCodeResendCooldown = TimeSpan.FromSeconds(60);
    private const int ReauthPasswordMaxAttempts = 5;
    private static readonly TimeSpan ReauthPasswordWindow = TimeSpan.FromMinutes(15);

    private readonly IdentityContext _context;

    public AuthPropertiesStorage(IdentityContext context)
    {
        _context = context;
    }

    public async Task<bool> CheckOtpEnabled(long userId)
    {
        var props = await _context.AuthUserProperties.FirstOrDefaultAsync(x => x.UserId == userId);

        return props is not null && props.OtpEnabled;
    }

    /// <summary>
    /// Сохраняет секрет Authenticator как ожидающий подтверждения. Действующие <c>OtpSecret</c>/<c>OtpEnabled</c> не меняются.
    /// </summary>
    public async Task SetPendingOtpSecret(long userId, string secretKey, DateTime expiresAt)
    {
        var props = await _context.AuthUserProperties.FirstOrDefaultAsync(x => x.UserId == userId);

        if (props is null)
        {
            props = new AuthUserProperty()
            {
                OtpEnabled = false,
                UserId = userId,
                PendingOtpSecret = secretKey,
                PendingOtpSecretExpiresAt = expiresAt
            };

            await _context.AuthUserProperties.AddAsync(props);
            await _context.SaveChangesAsync();

            return;
        }

        props.PendingOtpSecret = secretKey;
        props.PendingOtpSecretExpiresAt = expiresAt;

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Атомарно делает подтверждённый ожидающий секрет действующим и включает Authenticator.
    /// Возвращает false, если ожидающий секрет успели заменить или сбросить после проверки кода.
    /// </summary>
    public async Task<bool> ActivatePendingOtpSecret(long userId, string verifiedSecret)
    {
        var updated = await _context.AuthUserProperties
            .Where(x => x.UserId == userId && x.PendingOtpSecret == verifiedSecret)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.OtpSecret, x => x.PendingOtpSecret)
                .SetProperty(x => x.OtpEnabled, true)
                .SetProperty(x => x.PendingOtpSecret, (string?)null)
                .SetProperty(x => x.PendingOtpSecretExpiresAt, (DateTime?)null));

        return updated == 1;
    }

    /// <summary>
    /// Атомарно занимает одну попытку ввода пароля при повторной аутентификации (до самой проверки пароля —
    /// параллельный перебор не превысит лимит). Не более <see cref="ReauthPasswordMaxAttempts"/> попыток за окно
    /// <see cref="ReauthPasswordWindow"/>, которое открывается первой попыткой. Возвращает false, если лимит окна исчерпан.
    /// </summary>
    public async Task<bool> TryReserveReauthPasswordAttempt(long userId)
    {
        var now = DateTime.UtcNow;
        DateTime? newWindowEndsAt = now + ReauthPasswordWindow;

        var reserved = await _context.AuthUserProperties
            .Where(x => x.UserId == userId
                        && (x.ReauthPasswordWindowEndsAt == null
                            || x.ReauthPasswordWindowEndsAt <= now
                            || x.ReauthPasswordAttempts < ReauthPasswordMaxAttempts))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ReauthPasswordAttempts,
                    x => x.ReauthPasswordWindowEndsAt == null || x.ReauthPasswordWindowEndsAt <= now
                        ? 1
                        : x.ReauthPasswordAttempts + 1)
                .SetProperty(x => x.ReauthPasswordWindowEndsAt,
                    x => x.ReauthPasswordWindowEndsAt == null || x.ReauthPasswordWindowEndsAt <= now
                        ? newWindowEndsAt
                        : x.ReauthPasswordWindowEndsAt));

        if (reserved == 1)
        {
            return true;
        }

        // Строка есть, но лимит окна исчерпан.
        if (await _context.AuthUserProperties.AnyAsync(x => x.UserId == userId))
        {
            return false;
        }

        // У пользователя ещё нет настроек 2FA: создаём строку сразу с первой занятой попыткой.
        await _context.AuthUserProperties.AddAsync(new AuthUserProperty
        {
            UserId = userId,
            ReauthPasswordAttempts = 1,
            ReauthPasswordWindowEndsAt = newWindowEndsAt
        });
        await _context.SaveChangesAsync();

        return true;
    }

    /// <summary>Сбрасывает счётчик попыток после успешной проверки пароля.</summary>
    public async Task ResetReauthPasswordAttempts(long userId)
    {
        await _context.AuthUserProperties
            .Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ReauthPasswordAttempts, 0)
                .SetProperty(x => x.ReauthPasswordWindowEndsAt, (DateTime?)null));
    }

    public async Task<string?> GetOtpSecretKey(long userId)
    {
        var props = await _context.AuthUserProperties.FirstOrDefaultAsync(x => x.UserId == userId);

        if (props is null)
        {
            throw new OtpNotCreatedException();
        }

        return props.OtpSecret;
    }

    public async Task EnableEmailOtp(long userId)
    {
        var props = await _context.AuthUserProperties.FirstOrDefaultAsync(x => x.UserId == userId);

        if (props is null)
        {
            throw new OtpNotCreatedException();
        }

        props.EmailOtpEnabled = true;

        await _context.SaveChangesAsync();
    }

    public async Task<AuthUserProperty?> GetUserAuthProperties(long userId)
    {
        return await _context.AuthUserProperties.Where(x => x.UserId == userId).FirstOrDefaultAsync();
    }

    public async Task DisableOtp(long userId)
    {
        var props = await _context.AuthUserProperties.FirstOrDefaultAsync(x => x.UserId == userId);

        if (props is null)
        {
            throw new OtpNotCreatedException();
        }

        props.OtpEnabled = false;
        props.PendingOtpSecret = null;
        props.PendingOtpSecretExpiresAt = null;

        await _context.SaveChangesAsync();
    }

    public async Task DisableEmailOtp(long userId)
    {
        var props = await _context.AuthUserProperties.FirstOrDefaultAsync(x => x.UserId == userId);

        if (props is null)
        {
            throw new OtpNotCreatedException();
        }

        props.EmailOtpEnabled = false;

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Выдаёт одноразовый email-код: заменяет прежний, задаёт срок и обнуляет счётчик попыток.
    /// Возвращает false, если код того же назначения уже выдан менее <see cref="EmailAuthCodeResendCooldown"/> назад —
    /// тогда прежний код остаётся в силе, а новое письмо отправлять не нужно.
    /// </summary>
    public async Task<bool> TryIssueEmailAuthCode(long userId, EmailAuthCodePurpose purpose, string code)
    {
        var now = DateTime.UtcNow;
        var cooldownBorder = now - EmailAuthCodeResendCooldown;
        var expiresAt = now + EmailAuthCodeLifetime;

        var updated = await _context.AuthUserProperties
            .Where(x => x.UserId == userId
                        && (x.EmailAuthCodePurpose != purpose
                            || x.EmailAuthCodeIssuedAt == null
                            || x.EmailAuthCodeIssuedAt <= cooldownBorder))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastEmailAuthCode, code)
                .SetProperty(x => x.EmailAuthCodePurpose, (EmailAuthCodePurpose?)purpose)
                .SetProperty(x => x.EmailAuthCodeIssuedAt, (DateTime?)now)
                .SetProperty(x => x.EmailAuthCodeExpiresAt, (DateTime?)expiresAt)
                .SetProperty(x => x.EmailAuthCodeAttempts, 0));

        if (updated == 1)
        {
            return true;
        }

        // Строка есть, но сработал cooldown.
        if (await _context.AuthUserProperties.AnyAsync(x => x.UserId == userId))
        {
            return false;
        }

        await _context.AuthUserProperties.AddAsync(new AuthUserProperty
        {
            UserId = userId,
            LastEmailAuthCode = code,
            EmailAuthCodePurpose = purpose,
            EmailAuthCodeIssuedAt = now,
            EmailAuthCodeExpiresAt = expiresAt
        });
        await _context.SaveChangesAsync();

        return true;
    }

    /// <summary>
    /// Проверяет и атомарно расходует email-код. Возвращает false, если кода нет, он просрочен,
    /// выдан для другого назначения, исчерпал попытки, не совпал или его уже использовал параллельный запрос.
    /// </summary>
    public async Task<bool> TryConsumeEmailAuthCode(long userId, EmailAuthCodePurpose purpose, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        var now = DateTime.UtcNow;

        // Попытка списывается до сравнения: параллельный перебор не превысит лимит.
        var reserved = await _context.AuthUserProperties
            .Where(x => x.UserId == userId
                        && x.EmailAuthCodePurpose == purpose
                        && x.LastEmailAuthCode != null
                        && x.EmailAuthCodeExpiresAt > now
                        && x.EmailAuthCodeAttempts < EmailAuthCodeMaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EmailAuthCodeAttempts, x => x.EmailAuthCodeAttempts + 1));

        if (reserved != 1)
        {
            return false;
        }

        var stored = await _context.AuthUserProperties
            .Where(x => x.UserId == userId)
            .Select(x => x.LastEmailAuthCode)
            .FirstOrDefaultAsync();

        if (stored is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(code)))
        {
            return false;
        }

        // Атомарный захват: при параллельных запросах с верным кодом успех получает только один.
        var consumed = await _context.AuthUserProperties
            .Where(x => x.UserId == userId && x.EmailAuthCodePurpose == purpose && x.LastEmailAuthCode == stored)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastEmailAuthCode, (string?)null)
                .SetProperty(x => x.EmailAuthCodePurpose, (EmailAuthCodePurpose?)null)
                .SetProperty(x => x.EmailAuthCodeIssuedAt, (DateTime?)null)
                .SetProperty(x => x.EmailAuthCodeExpiresAt, (DateTime?)null)
                .SetProperty(x => x.EmailAuthCodeAttempts, 0));

        return consumed == 1;
    }

    public async Task UpdateOptType(OtpType type, long userId)
    {
        var props = await _context.AuthUserProperties.FirstOrDefaultAsync(x => x.UserId == userId);

        if (props is null)
        {
            throw new OtpNotCreatedException();
        }

        props.SelectedOtpType = type;

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Удаляет 2FA-свойства пользователя (при удалении аккаунта).
    /// </summary>
    public async Task DeleteByUserId(long userId)
    {
        await _context.AuthUserProperties.Where(x => x.UserId == userId).ExecuteDeleteAsync();
    }
}