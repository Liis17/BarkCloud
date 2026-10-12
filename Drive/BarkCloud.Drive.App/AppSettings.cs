using System.IO;
using System.Text.Json;

namespace BarkCloud.Drive.App;

// Настройки UI: последняя использованная буква диска — чтобы автомонтирование
// при следующем запуске поднимало тот же диск. Файл: %LOCALAPPDATA%\BarkCloud.Drive\app.json
internal sealed class AppSettings
{
    public bool Configured { get; set; }   // первичная настройка (мастер) пройдена
    public string? DriveName { get; set; }  // метка тома (имя диска)
    public string? DriveLetter { get; set; }
    public string? Language { get; set; }   // код языка UI (ru/en/de); null → авто по Windows

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BarkCloud.Drive", "app.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // повреждён / нет доступа — стартуем с дефолтами
        }

        return new AppSettings();
    }

    public void Save()
    {
        var tempFile = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(tempFile, JsonSerializer.Serialize(this));
            File.Move(tempFile, FilePath, overwrite: true);
        }
        catch
        {
            // не критично
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
