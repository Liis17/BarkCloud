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

    // Физический диск сервера: всего, занято не-S3 данными, занято S3 (облаком).
    // Свободное = DiskTotalBytes - DiskOtherBytes - DiskS3Bytes.
    public long DiskTotalBytes { get; set; }
    public long DiskOtherBytes { get; set; }
    public long DiskS3Bytes { get; set; }

    public string? Message { get; set; }
    public string? Error { get; set; }

    // Последняя ошибка синхронизации файла в облако (Cleanup/PersistSession) — чтобы не глушить её молча.
    public string? LastSyncError { get; set; }

    // Профиль/окружение для дашборда.
    public string? Username { get; set; }
    public string? ServerHost { get; set; }
    public string? VolumeLabel { get; set; }
}
