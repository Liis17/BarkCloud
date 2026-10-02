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

    /// <summary>Жёсткий предел HS256 в IdentityModel: ниже 128 бит токены не подписываются и не проверяются.</summary>
    public const int MinStartupBytes = 16;

    /// <summary>Рекомендация RFC 7518 §3.2 (размер ключа не меньше хеша, 256 бит) — для новых значений в Configuration.</summary>
    public const int RecommendedMinBytes = 32;

    /// <summary>
    /// Возвращает UTF-8 байты секрета. Пустой секрет или короче <paramref name="minBytes"/> байт —
    /// <see cref="InvalidOperationException"/> с понятным сообщением (без значения секрета).
    /// </summary>
    public static byte[] GetKeyBytes(string? secret, int minBytes = MinStartupBytes)
    {
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException($"{ConfigKey} не задан. Задайте секрет JWT длиной не менее {minBytes} байт (UTF-8).");

        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length < minBytes)
            throw new InvalidOperationException(
                $"{ConfigKey} слишком короткий: {bytes.Length} байт (UTF-8), требуется не менее {minBytes}. " +
                "Кириллица и другие не-ASCII символы занимают больше одного байта.");

        return bytes;
    }
}
