using System.Text.Json;

namespace BarkCloud.Drive.Engine;

// Настройки движка (папка кэша). Это просто путь — храним плоским JSON без шифрования.
// Файл: %LOCALAPPDATA%\BarkCloud.Drive\settings.json. По умолчанию кэш — во временной папке.
internal sealed class EngineSettingsStore
{
    private readonly string _file;
    private readonly string _defaultCacheDir;

    public EngineSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BarkCloud.Drive");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "settings.json");
        _defaultCacheDir = Path.Combine(Path.GetTempPath(), "BarkCloudDrive");
    }

    public string GetCacheDir()
    {
        try
        {
            if (File.Exists(_file))
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file));
                if (!string.IsNullOrWhiteSpace(stored?.CacheDir))
                    return stored!.CacheDir!;
            }
        }
        catch
        {
            // повреждён — откатываемся к дефолту
        }

        return _defaultCacheDir;
    }

    public void SetCacheDir(string path)
        => File.WriteAllText(_file, JsonSerializer.Serialize(new Stored { CacheDir = path }));

    private sealed class Stored
    {
        public string? CacheDir { get; set; }
    }
}
