namespace BarkCloud.Drive.Contracts;

// DTO состояния движка, гоняемый по IPC. Обычный класс с get/set —
// корректно сериализуется StreamJsonRpc.
public sealed class EngineStatus
{
    public bool Authenticated { get; set; }
    public bool Mounted { get; set; }
    public string? DriveLetter { get; set; }
    public long UsedBytes { get; set; }
    public long LimitBytes { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }

    // Последняя ошибка синхронизации файла в облако (Cleanup/PersistSession) — чтобы не глушить её молча.
    public string? LastSyncError { get; set; }

    // Профиль/окружение для дашборда.
    public string? Username { get; set; }
    public string? ServerHost { get; set; }
    public string? VolumeLabel { get; set; }
}
