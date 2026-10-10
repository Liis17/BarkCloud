using System.Text;

namespace BarkCloud.Shared.Identity;

/// <summary>
/// Единственный способ превратить <c>JwtSettings:SecretKey</c> в байты HMAC-ключа: UTF-8.
/// Все, кто подписывает или проверяет JWT и HMAC на этом секрете, обязаны брать байты отсюда —
/// иначе при не-ASCII секрете ключи расходятся (ASCII заменяет символы на «?»).
/// </summary>
public static class JwtSecret
{
    public const string ConfigKey = "JwtSettings:SecretKey";

    /// <summary>
    /// Минимум HS256: signer IdentityModel отклоняет ключ короче 256 бит (<c>IDX10720</c>),
    /// что совпадает с RFC 7518 §3.2. Он общий для запуска, подписи, проверки и сохранения в Configuration.
    /// </summary>
    public const int MinKeyBytes = 32;

    /// <summary>
    /// Возвращает UTF-8 байты секрета. Пустой секрет или короче <see cref="MinKeyBytes"/> байт —
    /// <see cref="InvalidOperationException"/> с понятным сообщением (без значения секрета).
    /// </summary>
    public static byte[] GetKeyBytes(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException($"{ConfigKey} не задан. Задайте секрет JWT длиной не менее {MinKeyBytes} байт (UTF-8).");

        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length < MinKeyBytes)
            throw new InvalidOperationException(
                $"{ConfigKey} слишком короткий: {bytes.Length} байт (UTF-8), требуется не менее {MinKeyBytes}. " +
                "Кириллица и другие не-ASCII символы занимают больше одного байта.");

        return bytes;
    }
}
