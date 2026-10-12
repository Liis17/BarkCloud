using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BarkCloud.Drive.Engine;

// Персист refresh-токена через DPAPI (шифрование на текущего пользователя Windows) —
// Windows-аналог iOS Keychain. Access-токен не храним: он короткоживущий и всегда
// восстанавливается из refresh. Файл: %LOCALAPPDATA%\BarkCloud.Drive\refresh.bin
internal sealed class TokenStore
{
    private readonly string _file;
    private readonly string? _serverKey;
    private readonly object _lock = new();

    public TokenStore(string? dir = null, string? serverKey = null)
    {
        dir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarkCloud.Drive");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "refresh.bin");
        _serverKey = serverKey;
    }

    public void SaveRefreshToken(string token)
    {
        lock (_lock)
            SaveRefreshTokenLocked(token);
    }

    public string? LoadRefreshToken()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_file))
                    return null;

                var decrypted = ProtectedData.Unprotect(
                    File.ReadAllBytes(_file), optionalEntropy: null, DataProtectionScope.CurrentUser);
                StoredToken? stored;
                try { stored = JsonSerializer.Deserialize<StoredToken>(decrypted); }
                catch (JsonException) { stored = null; }
                if (stored?.Token is { Length: > 0 })
                {
                    if (_serverKey is not null
                        && !string.Equals(stored.ServerKey, _serverKey, StringComparison.Ordinal))
                    {
                        ClearLocked();
                        return null;
                    }

                    return stored.Token;
                }

                // Старые версии хранили в DPAPI только UTF-8 refresh-токен. При первом
                // чтении привязываем его к текущей конфигурации без повторного входа.
                var legacyToken = Encoding.UTF8.GetString(decrypted);
                if (string.IsNullOrEmpty(legacyToken))
                    return null;
                if (_serverKey is not null)
                {
                    try { SaveRefreshTokenLocked(legacyToken); }
                    catch (IOException) { /* Сохранение повторится при следующем чтении. */ }
                }
                return legacyToken;
            }
            catch
            {
                return null; // повреждён / не наш профиль
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
            ClearLocked();
    }

    private void SaveRefreshTokenLocked(string token)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new StoredToken
        {
            ServerKey = _serverKey,
            Token = token,
        });
        var encrypted = ProtectedData.Protect(
            serialized, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_file, encrypted);
    }

    private void ClearLocked()
    {
        try
        {
            if (File.Exists(_file))
                File.Delete(_file);
        }
        catch
        {
            // не критично
        }
    }

    private sealed class StoredToken
    {
        public string? ServerKey { get; set; }
        public string? Token { get; set; }
    }
}
