using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

public sealed class UploadArtifactCleaner(
    FilesContext context,
    ITrashPurgeService purge,
    ILogger<UploadArtifactCleaner> logger) : IUploadArtifactCleaner
{
    public async Task CleanupAsync(UploadSession session, CancellationToken cancellationToken)
    {
        // Владелец снимается с оригинала и неиспользуемых производных блобов в одной транзакции.
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            await context.RemoveUploaderAsync(session.FileId, session.OwnerId, cancellationToken);
            await context.ReleaseDerivedBlobOwnerAsync([session.FileId], session.OwnerId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        try
        {
            await purge.PurgeOrphanBlobsAsync([session.FileId], cancellationToken);
            session.CleanupPending = await context.UploadedFiles
                .AsNoTracking()
                .AnyAsync(x => x.Id == session.FileId, cancellationToken);
        }
        catch (Exception error)
        {
            session.CleanupPending = true;
            logger.LogWarning(
                error,
                "Артефакты неуспешной upload-сессии {SessionId} будут удалены повторно",
                session.Id);
        }

        session.UpdatedAt = DateTime.UtcNow;
        session.ConcurrencyToken = Guid.NewGuid();
        await context.SaveChangesAsync(cancellationToken);
    }
}
