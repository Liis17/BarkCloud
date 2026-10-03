namespace BarkCloud.Files.Services;

public interface IPhysicalStorageStatsProvider
{
    Task<PhysicalStorageStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<PhysicalStorageStats> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
