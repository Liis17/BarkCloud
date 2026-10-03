namespace BarkCloud.Files.Services;

public interface IS3StorageStatsProvider
{
    Task<S3StorageStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<S3StorageStats> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed record S3StorageStats(
    long UsedBytes,
    long QuotaBytes,
    bool HasFiniteQuota,
    bool IsAvailable)
{
    public string State { get; init; } = "ready";
    public DateTimeOffset? UpdatedAt { get; init; }

    public static S3StorageStats Unavailable { get; } = new(0, 0, false, false);
}
