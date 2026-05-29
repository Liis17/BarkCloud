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
}
