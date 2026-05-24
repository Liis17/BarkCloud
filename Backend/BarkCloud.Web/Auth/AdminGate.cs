using System.Security.Cryptography;
using System.Text;

using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Auth;

/// <summary>
/// Гейт администраторских действий (обновление бэкенда) по отдельному паролю из конфигурации.
/// В BarkCloud нет ролей, а облако self-hosted «для своих», поэтому доступ открывается вводом
/// пароля <c>App:AdminPassword</c>: после проверки выдаётся подписанная HttpOnly-cookie
/// <see cref="AdminCookie"/> (HMAC-SHA256 на общем <c>JwtSettings:SecretKey</c>), и последующие
/// запросы к <c>/api/system/*</c> не требуют повторного ввода до истечения срока.
/// </summary>
public sealed class AdminGate
{
    public const string AdminCookie = "bark_admin";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly byte[]? _key;
    private readonly string? _password;
    private readonly bool _cookieSecure;

    public AdminGate(IConfiguration configuration)
    {
        var secret = configuration["JwtSettings:SecretKey"];
        _key = string.IsNullOrEmpty(secret) ? null : Encoding.ASCII.GetBytes(secret);
        _password = configuration["App:AdminPassword"];
        _cookieSecure = configuration.Flag("App:CookieSecure");
    }

    /// <summary>Настроен ли админ-доступ (задан пароль и есть секрет для подписи).</summary>
    public bool Enabled => !string.IsNullOrEmpty(_password) && _key is not null;

    /// <summary>Проверить пароль и при успехе выдать админ-cookie. Возвращает false при неверном пароле.</summary>
    public bool Unlock(HttpContext http, string? password)
    {
        if (!Enabled || string.IsNullOrEmpty(password)) return false;
        if (!FixedTimeEquals(password, _password!)) return false;

        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        http.Response.Cookies.Append(AdminCookie, Sign(expires), new CookieOptions
        {
            HttpOnly = true,
            Secure = _cookieSecure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expires
        });
        return true;
    }

    /// <summary>Валидна ли админ-cookie (подпись совпадает и срок не истёк).</summary>
    public bool IsUnlocked(HttpContext http)
    {
        if (!Enabled) return false;

        var raw = http.Request.Cookies[AdminCookie];
        if (string.IsNullOrEmpty(raw)) return false;

        var dot = raw.IndexOf('.');
        if (dot <= 0) return false;

        var expPart = raw[..dot];
        if (!long.TryParse(expPart, out var expUnix)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expUnix) <= DateTimeOffset.UtcNow) return false;

        var expected = Sign(DateTimeOffset.FromUnixTimeSeconds(expUnix));
        return FixedTimeEquals(raw, expected);
    }

    public void Lock(HttpContext http)
        => http.Response.Cookies.Delete(AdminCookie, new CookieOptions { Path = "/", Secure = _cookieSecure, SameSite = SameSiteMode.Lax });

    private string Sign(DateTimeOffset expires)
    {
        var exp = expires.ToUnixTimeSeconds();
        var sig = HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes($"admin:{exp}"));
        return $"{exp}.{Convert.ToBase64String(sig)}";
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
