namespace BarkCloud.Files.Services;

public sealed record StorageQuotaSnapshot(long? LimitBytes, long UsedBytes, long ReservedBytes)
{
    public void EnsureCanReserve(long requestedBytes)
    {
        if (requestedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedBytes));
        if (LimitBytes.HasValue && UsedBytes + ReservedBytes + requestedBytes > LimitBytes.Value)
            throw new BarkCloud.Shared.Exceptions.Files.UploadQuotaExceededException(
                LimitBytes.Value,
                UsedBytes,
                ReservedBytes,
                requestedBytes);
    }
}

public interface IStorageQuotaService
{
    Task<StorageQuotaSnapshot> GetSnapshotAsync(
        long ownerId,
        bool acquireTransactionLock,
        CancellationToken cancellationToken);
}
