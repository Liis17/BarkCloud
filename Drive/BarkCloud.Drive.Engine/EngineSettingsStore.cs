using System.Text.Json;

namespace BarkCloud.Drive.Engine;

// Настройки движка: папка кэша и последний смонтированный диск (буква+метка) —
// чтобы при автозапуске движка диск поднимался без участия UI. Плоский JSON без
// шифрования. Файл: %LOCALAPPDATA%\BarkCloud.Drive\settings.json.
internal sealed class EngineSettingsStore
{
    private readonly string _file;
    private readonly string _defaultCacheDir;
    private readonly Stored _data;
    private readonly object _ownerLock = new();

    public EngineSettingsStore(string? dir = null)
    {
        var hasCustomDir = dir is not null;
        dir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarkCloud.Drive");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "settings.json");
        _defaultCacheDir = hasCustomDir
            ? Path.Combine(dir, "cache")
            : Path.Combine(Path.GetTempPath(), "BarkCloudDrive");
        _data = Load();
    }

    public string GetCacheDir()
        => string.IsNullOrWhiteSpace(_data.CacheDir) ? _defaultCacheDir : _data.CacheDir!;

    public void SetCacheDir(string path)
    {
        _data.CacheDir = path;
        Save();
    }

    public (string? Letter, string? Label) GetLastMount() => (_data.LastDriveLetter, _data.LastVolumeLabel);

    public string? GetLastUserId(string serverKey)
    {
        lock (_ownerLock)
            return _data.LastUserIds.GetValueOrDefault(serverKey);
    }

    public void SetLastUserId(string serverKey, string userId)
    {
        lock (_ownerLock)
        {
            _data.LastUserIds[serverKey] = userId;
            Save();
        }
    }

    public void ClearLastUserId(string serverKey)
    {
        lock (_ownerLock)
        {
            _data.LastUserIds.Remove(serverKey);
            Save();
        }
    }

    public void SetLastMount(string? letter, string? label)
    {
        if (!string.IsNullOrEmpty(letter)) _data.LastDriveLetter = letter;
        if (!string.IsNullOrEmpty(label)) _data.LastVolumeLabel = label;
        Save();
    }

    private Stored Load()
    {
        try
        {
            if (File.Exists(_file))
                return JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file)) ?? new Stored();
        }
        catch
        {
            // повреждён — стартуем с дефолтами
        }

        return new Stored();
    }

    private void Save()
    {
        var tempFile = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempFile, JsonSerializer.Serialize(_data));
            File.Move(tempFile, _file, overwrite: true);
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
                // очистка временного файла не должна влиять на сохранение настроек
            }
        }
    }

    private sealed class Stored
    {
        public string? CacheDir { get; set; }
        public string? LastDriveLetter { get; set; }
        public string? LastVolumeLabel { get; set; }
        public Dictionary<string, string> LastUserIds { get; set; } = new(StringComparer.Ordinal);
    }
}
