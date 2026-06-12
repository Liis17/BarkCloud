namespace BarkCloud.Files.Services;

public interface IPhysicalStorageStatsProvider
{
    Task<PhysicalStorageStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
