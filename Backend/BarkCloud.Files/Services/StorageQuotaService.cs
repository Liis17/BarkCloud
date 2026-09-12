using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

public sealed class StorageQuotaService(FilesContext context, IStorageLimitProvider limits)
    : IStorageQuotaService
{
    public async Task<StorageQuotaSnapshot> GetSnapshotAsync(
        long ownerId,
        bool acquireTransactionLock,
        CancellationToken cancellationToken)
    {
        // Не держим PostgreSQL advisory lock во время сетевого вызова Users.
        var limit = await limits.GetLimitBytesAsync(ownerId, cancellationToken);

        if (acquireTransactionLock
            && context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({ownerId})",
                cancellationToken);
        }

        var used = await context.UploadedFiles
            .AsNoTracking()
            .WhereReady()
            .Where(x => x.Uploaders.Contains(ownerId))
            .SumAsync(x => (long?)x.Size, cancellationToken) ?? 0;
        var reserved = await context.UploadSessions
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId
                        && (x.Status == UploadSessionStatus.Uploading
                            || x.Status == UploadSessionStatus.Processing))
            .SumAsync(x => (long?)x.ReservedBytes, cancellationToken) ?? 0;

        return new StorageQuotaSnapshot(limit, used, reserved);
    }
}
