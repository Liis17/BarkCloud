namespace BarkCloud.Drive.Contracts;

// Настройки движка, редактируемые из UI. Папка кэша — куда движок складывает
// скачанное содержимое файлов.
public sealed class EngineSettings
{
    public string CacheDir { get; set; } = "";
}
