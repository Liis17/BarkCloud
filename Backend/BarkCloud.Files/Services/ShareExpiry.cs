using System.Security.Cryptography;

using BarkCloud.Files.Domain;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

namespace BarkCloud.Files.Services;

/// <summary>Срок действия публичных ссылок: проверка запроса, активность и повторная публикация.</summary>
public static class ShareExpiry
{
    /// <summary>Проверяет срок из запроса: <c>null</c> — бессрочно, иначе момент строго после <paramref name="utcNow"/>.</summary>
    public static DateTime? Validate(DateTime? requested, DateTime utcNow)
    {
        if (requested is null)
            return null;

        var expiresAt = requested.Value.ToUniversalTime();
        if (expiresAt <= utcNow)
            throw new ShareExpiryInvalidException();
        return expiresAt;
    }

    public static Timestamp? ToTimestamp(DateTime? expiresAt) =>
        expiresAt is { } value ? Timestamp.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;

    public static bool IsExpired(DateTime? expiresAt, DateTime utcNow) => expiresAt is { } value && value <= utcNow;

    /// <summary>
    /// Повторная публикация объекта с уже существующей ссылкой. Истёкшая ссылка получает новый токен,
    /// дату создания и счётчик: старый URL не должен снова заработать. Срок заменяется, если
    /// <paramref name="replaceExpiry"/> или ссылка истекла; иначе остаётся прежним.
    /// </summary>
    /// <returns><c>true</c>, если ссылку нужно сохранить.</returns>
    public static bool Republish(IPublicShareLink link, DateTime? expiresAt, bool replaceExpiry, DateTime utcNow)
    {
        if (IsExpired(link.ExpiresAt, utcNow))
        {
            link.Token = GenerateToken();
            link.CreatedAt = utcNow;
            link.ClickCount = 0;
            link.ExpiresAt = expiresAt;
            return true;
        }

        if (!replaceExpiry || link.ExpiresAt == expiresAt)
            return false;

        link.ExpiresAt = expiresAt;
        return true;
    }

    /// <summary>URL-safe токен из 16 случайных байт (base64url без паддинга).</summary>
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
