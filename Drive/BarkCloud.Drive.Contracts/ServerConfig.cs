using System.IO;
using System.Text.Json;

namespace BarkCloud.Drive.Contracts;

// Адреса self-hosted сервера, заданные пользователем при первичной настройке.
// Общий тип для App (пишет/читает в мастере и настройках) и Engine (читает на старте).
// Файл: %LOCALAPPDATA%\BarkCloud.Drive\server.json. Пока файла нет — Engine берёт
// дефолты из appsettings.json, а UI показывает значения по умолчанию отсюда.
public sealed class ServerConfig
{
    public string Host { get; set; } = "cloud.barkfluff.com";
    public int IdentityPort { get; set; } = 7020;
    public int FilesPort { get; set; } = 7025;
    public int UsersPort { get; set; } = 7021;
    public bool AcceptAnyCert { get; set; } = false;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BarkCloud.Drive", "server.json");

    // null, если пользователь ещё не задавал адреса (файла нет / повреждён).
    public static ServerConfig? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ServerConfig>(
                    File.ReadAllText(FilePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            // повреждён — считаем, что адрес не задан
        }

        return null;
    }

    // Кидает при ошибке записи — вызывающий должен показать сообщение
    // (без сохранения перезапуск движка не подхватит новый адрес).
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tempFile = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempFile, JsonSerializer.Serialize(this));
            File.Move(tempFile, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch
            {
                // Очистка временного файла не должна влиять на сохранение настроек.
            }
        }
    }
}
