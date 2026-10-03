namespace BarkCloud.Files.Services;

public sealed record PhysicalStorageStats(
    long TotalBytes,
    long AvailableFreeBytes,
    long DiskUsedWithoutS3Bytes,
    long S3UsedBytes)
{
    public string State { get; init; } = "ready";
    public DateTimeOffset? UpdatedAt { get; init; }
}
