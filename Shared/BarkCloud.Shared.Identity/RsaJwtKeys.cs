using Microsoft.IdentityModel.Tokens;

using System.Security.Cryptography;

namespace BarkCloud.Shared.Identity;

/// <summary>
/// Загрузка RSA-ключей JWT RS256: приватный ключ есть только у Identity (подпись),
/// публичные — у всех валидаторов. Общий HMAC-секрет (<c>JwtSettings:SecretKey</c>) упразднён:
/// с ним любой сервис мог выпустить токен за любой другой (аудит безопасности, п. 9).
/// Ключи передаются PEM-файлами; путь — из конфигурации или переменной окружения.
/// </summary>
public static class RsaJwtKeys
{
    public const string PrivateKeyConfigKey = "JwtSettings:PrivateKeyFile";
    public const string PublicKeyConfigKey = "JwtSettings:PublicKeyFile";

    public const string PrivateKeyEnvName = "JWT_PRIVATE_KEY_FILE";
    public const string PublicKeyEnvName = "JWT_PUBLIC_KEY_FILE";

    /// <summary>Приватный ключ подписи слабее RSA-3072 отклоняется на старте.</summary>
    public const int MinSigningKeyBits = 3072;

    /// <summary>Публичный ключ проверки слабее RSA-2048 отклоняется на старте.</summary>
    public const int MinPublicKeyBits = 2048;

    /// <summary>
    /// Загружает приватный ключ подписи (PKCS#1 «RSA PRIVATE KEY» или PKCS#8 «PRIVATE KEY»).
    /// Значение — путь к PEM-файлу. Бросает <see cref="InvalidOperationException"/> с указанием,
    /// какой ключ/переменную задать, если путь пуст, файл не читается или ключ слишком слабый.
    /// </summary>
    public static RsaSecurityKey LoadSigningKey(string? fileValue)
    {
        var rsa = LoadRsa(fileValue, PrivateKeyConfigKey, PrivateKeyEnvName, expectPrivate: true);

        if (rsa.KeySize < MinSigningKeyBits)
            throw new InvalidOperationException(
                $"Приватный JWT-ключ ({PrivateKeyConfigKey}/{PrivateKeyEnvName}) — RSA-{rsa.KeySize}, " +
                $"требуется не менее {MinSigningKeyBits} бит. Сгенерируйте новую пару и перезапустите сервисы.");

        var key = new RsaSecurityKey(rsa) { KeyId = ComputeKid(rsa) };
        return key;
    }

    /// <summary>
    /// Загружает набор публичных ключей проверки (SPKI «PUBLIC KEY»): один путь или несколько
    /// через «;» — на время ротации новый ключ добавляют рядом со старым. Возвращает словарь
    /// kid → ключ; валидатор подбирает ключ по kid из заголовка токена.
    /// </summary>
    public static IReadOnlyDictionary<string, RsaSecurityKey> LoadPublicKeyMap(string? filesValue)
    {
        if (string.IsNullOrWhiteSpace(filesValue))
            throw new InvalidOperationException(
                $"Не задан публичный JWT-ключ. Укажите путь к PEM-файлу в {PublicKeyConfigKey} " +
                $"или переменной {PublicKeyEnvName} (несколько путей — через «;»).");

        var keys = new Dictionary<string, RsaSecurityKey>(StringComparer.Ordinal);

        foreach (var rawPath in filesValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rsa = LoadRsa(rawPath, PublicKeyConfigKey, PublicKeyEnvName, expectPrivate: false);

            if (rsa.KeySize < MinPublicKeyBits)
                throw new InvalidOperationException(
                    $"Публичный JWT-ключ «{rawPath}» — RSA-{rsa.KeySize}, требуется не менее {MinPublicKeyBits} бит.");

            var key = new RsaSecurityKey(rsa) { KeyId = ComputeKid(rsa) };
            if (!keys.TryAdd(key.KeyId, key))
                throw new InvalidOperationException(
                    $"Дубликат публичного JWT-ключа с kid {key.KeyId} в «{filesValue}»: " +
                    "один и тот же ключ указан дважды.");
        }

        if (keys.Count == 0)
            throw new InvalidOperationException(
                $"Не удалось загрузить ни одного публичного JWT-ключа из «{filesValue}» " +
                $"({PublicKeyConfigKey}/{PublicKeyEnvName}).");

        return keys;
    }

    /// <summary>kid — первые 16 hex-символов SHA-256 от DER-представления публичного ключа.</summary>
    public static string ComputeKid(RSA rsa) =>
        Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();

    private static RSA LoadRsa(string? fileValue, string configKey, string envName, bool expectPrivate)
    {
        if (string.IsNullOrWhiteSpace(fileValue))
            throw new InvalidOperationException(
                $"Не задан {(expectPrivate ? "приватный" : "публичный")} JWT-ключ. Укажите путь к PEM-файлу " +
                $"в {configKey} или переменной {envName}.");

        string pem;
        try
        {
            pem = File.ReadAllText(fileValue);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"JWT-ключ «{fileValue}» ({configKey}/{envName}) не читается: {ex.Message}", ex);
        }

        // ImportFromPem молча принимает и публичную часть вместо приватной — проверяем тип PEM по метке.
        var expectedLabel = expectPrivate ? "PRIVATE KEY" : "PUBLIC KEY";
        if (!pem.Contains(expectedLabel, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"JWT-ключ «{fileValue}» не распознан как {expectedLabel} PEM.");

        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"JWT-ключ «{fileValue}» не распознан как {(expectPrivate ? "приватный (RSA PRIVATE KEY / PRIVATE KEY)" : "публичный (PUBLIC KEY)")} PEM: {ex.Message}",
                ex);
        }
    }
}
