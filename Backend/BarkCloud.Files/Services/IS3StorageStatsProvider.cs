namespace BarkCloud.Files.Services;

public interface IS3StorageStatsProvider
{
    Task<S3StorageStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<S3StorageStats> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed record S3StorageStats(
    IReadOnlyList<S3QuotaGroupStats> Groups,
    bool IsAvailable)
{
    public string State { get; init; } = "ready";
    public DateTimeOffset? UpdatedAt { get; init; }

    public static S3StorageStats Unavailable { get; } = new(Array.Empty<S3QuotaGroupStats>(), false);
}

public sealed record S3QuotaGroupStats(
    string Id,
    string Name,
    long UsedBytes,
    long QuotaBytes,
    int UnmeasuredBuckets,
    bool IsUngrouped);
