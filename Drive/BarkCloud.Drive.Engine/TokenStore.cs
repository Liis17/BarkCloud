using System.Security.Cryptography;
using System.Text;

namespace BarkCloud.Drive.Engine;

// Персист refresh-токена через DPAPI (шифрование на текущего пользователя Windows) —
// Windows-аналог iOS Keychain. Access-токен не храним: он короткоживущий и всегда
// восстанавливается из refresh. Файл: %LOCALAPPDATA%\BarkCloud.Drive\refresh.bin
internal sealed class TokenStore
{
    private readonly string _file;

    public TokenStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BarkCloud.Drive");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "refresh.bin");
    }

    public void SaveRefreshToken(string token)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_file, encrypted);
    }

    public string? LoadRefreshToken()
    {
        try
        {
            if (!File.Exists(_file))
                return null;

            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(_file), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null; // повреждён / не наш профиль
        }
    }

    public void Clear()
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
}
