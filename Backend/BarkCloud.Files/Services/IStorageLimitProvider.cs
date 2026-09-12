namespace BarkCloud.Files.Services;

public interface IStorageLimitProvider
{
    /// <summary>null means that the user has no logical storage limit.</summary>
    Task<long?> GetLimitBytesAsync(long ownerId, CancellationToken cancellationToken);
}
