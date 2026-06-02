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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // не критично
        }
    }
}
