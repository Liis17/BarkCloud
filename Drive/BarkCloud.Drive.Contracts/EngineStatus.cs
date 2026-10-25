namespace BarkCloud.Drive.Contracts;

// DTO состояния движка, гоняемый по IPC. Обычный класс с get/set —
// корректно сериализуется StreamJsonRpc.
public sealed class EngineStatus
{
    public bool Authenticated { get; set; }
    public bool Mounted { get; set; }
    public string? DriveLetter { get; set; }
    // Физический диск сервера: всего, занято не-S3 данными, занято S3 (облаком).
    // Свободное = DiskTotalBytes - DiskOtherBytes - DiskS3Bytes.
    public long DiskTotalBytes { get; set; }
    public long DiskOtherBytes { get; set; }
    public long DiskS3Bytes { get; set; }

    // Состояние статистики хранилища из снимков сервера (GetUserStorageInfo
    // с NonBlockingStats): loading / ready / refreshing / error (+ not_configured для S3).
    public string? PhysicalStatsState { get; set; }
    public DateTime? PhysicalStatsUpdatedAt { get; set; }
    public bool AllS3StatsAvailable { get; set; }
    public string? AllS3StatsState { get; set; }
    public DateTime? AllS3StatsUpdatedAt { get; set; }
    // Группы квот S3 (внешние хранилища и «Без группы»); null, когда нет данных.
    public List<S3GroupStatus>? S3Groups { get; set; }
    // Ошибка последнего обновления статистики — отдельно от данных: при сбое
    // цифры остаются последним сохранённым снимком, а здесь причина сбоя.
    // Заполняется и когда снимка нет совсем (тогда все поля статистики пусты).
    public string? StorageStatsError { get; set; }

    public string? Message { get; set; }
    public string? Error { get; set; }

    // Последняя ошибка синхронизации файла в облако (Cleanup/PersistSession) — чтобы не глушить её молча.
    public string? LastSyncError { get; set; }
    public int PendingUploads { get; set; }

    // Профиль/окружение для дашборда.
    public string? Username { get; set; }
    public string? ServerHost { get; set; }
    public string? VolumeLabel { get; set; }
}

// Использование одной группы квот S3: внешнее хранилище или «Без группы».
// QuotaBytes <= 0 — безлимитная группа (IsUngrouped — бакеты вне групп).
public sealed class S3GroupStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long UsedBytes { get; set; }
    public long QuotaBytes { get; set; }
    public bool IsUngrouped { get; set; }
    // Бакеты группы, размер которых сервер не смог замерить: занято/свободно — оценка.
    public int UnmeasuredBuckets { get; set; }
}
