namespace BarkCloud.Files.Services;

public sealed record PhysicalStorageStats(
    long TotalBytes,
    long AvailableFreeBytes,
    long DiskUsedWithoutS3Bytes,
    long S3UsedBytes);
