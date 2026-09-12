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
        var file = await context.UploadedFiles
            .FirstOrDefaultAsync(x => x.Id == session.FileId, cancellationToken);
        if (file is not null)
        {
            file.Uploaders.Clear();
        }

        var previewFileIds = await context.FilePreviews
            .AsNoTracking()
            .Where(x => x.OriginalFileId == session.FileId)
            .Select(x => x.PreviewFileId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (previewFileIds.Count > 0)
        {
            var previewFiles = await context.UploadedFiles
                .Where(x => previewFileIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            foreach (var preview in previewFiles)
            {
                var stillNeeded = await context.FilePreviews
                    .AsNoTracking()
                    .AnyAsync(x => x.PreviewFileId == preview.Id
                                   && x.OriginalFileId != session.FileId
                                   && context.UploadedFiles.Any(original =>
                                       original.Id == x.OriginalFileId
                                       && original.Uploaders.Contains(session.OwnerId)),
                        cancellationToken);
                if (!stillNeeded)
                    preview.Uploaders.Remove(session.OwnerId);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

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
