using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

public sealed class UploadSessionMaintenance(
    FilesContext context,
    IMultipartUploadStore objects,
    IUploadArtifactCleaner cleaner,
    TimeProvider time,
    ILogger<UploadSessionMaintenance> logger)
{
    public static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(7);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expired = await context.UploadSessions
            .Where(x => x.Status == UploadSessionStatus.Uploading && x.ExpiresAt <= now)
            .OrderBy(x => x.ExpiresAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var session in expired)
        {
            try
            {
                await objects.AbortAsync(
                    session.StorageProfileId,
                    session.FileId.ToString(),
                    session.MultipartUploadId,
                    cancellationToken);
                session.CleanupPending = false;
            }
            catch (Exception error)
            {
                session.CleanupPending = true;
                logger.LogWarning(
                    error,
                    "Отмена multipart просроченной upload-сессии {SessionId} будет повторена",
                    session.Id);
            }

            session.Status = UploadSessionStatus.Expired;
            session.ReservedBytes = 0;
            session.UploadTokenHash = string.Empty;
            session.UpdatedAt = now;
            session.ConcurrencyToken = Guid.NewGuid();

            if (!session.CleanupPending)
                await RemovePlaceholderAsync(session, cancellationToken);
        }
        if (expired.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Истекло upload-сессий: {Count}", expired.Count);
        }

        // Legacy upload выполняется синхронно, но резерв хранится в той же таблице.
        // После аварийного рестарта завершаем уже записанный файл либо освобождаем
        // зависший резерв и передаём возможные артефакты общей зачистке.
        var expiredLegacy = await context.UploadSessions
            .Where(x => x.Status == UploadSessionStatus.Processing
                        && x.MultipartUploadId == string.Empty
                        && x.ExpiresAt <= now)
            .OrderBy(x => x.ExpiresAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var session in expiredLegacy)
        {
            var file = await context.UploadedFiles
                .FirstOrDefaultAsync(x => x.Id == session.FileId, cancellationToken);
            var completed = file is not null
                            && session.LegacyProcessingCompletedAt.HasValue
                            && !string.IsNullOrEmpty(file.Etag)
                            && file.Size == session.DeclaredSize;
            if (completed && file!.UploadedAt is null)
                file.UploadedAt = now;
            session.Status = completed ? UploadSessionStatus.Ready : UploadSessionStatus.Failed;
            session.ReservedBytes = 0;
            session.UploadTokenHash = string.Empty;
            session.ErrorCode = completed ? null : "legacy_upload_expired";
            session.ErrorMessage = completed ? null : "Legacy upload не завершился до перезапуска Files";
            session.CleanupPending = !completed;
            session.UpdatedAt = now;
            session.ConcurrencyToken = Guid.NewGuid();
        }
        if (expiredLegacy.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Завершена проверка зависших legacy upload-сессий: {Count}", expiredLegacy.Count);
        }

        var pending = await context.UploadSessions
            .Where(x => x.CleanupPending
                        && (x.Status == UploadSessionStatus.Failed
                            || x.Status == UploadSessionStatus.Cancelled
                            || x.Status == UploadSessionStatus.Expired))
            .OrderBy(x => x.UpdatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var session in pending)
        {
            if (session.Status == UploadSessionStatus.Failed)
            {
                await cleaner.CleanupAsync(session, cancellationToken);
                continue;
            }

            try
            {
                if (!string.IsNullOrEmpty(session.MultipartUploadId))
                {
                    await objects.AbortAsync(
                        session.StorageProfileId,
                        session.FileId.ToString(),
                        session.MultipartUploadId,
                        cancellationToken);
                }
                session.CleanupPending = false;
                session.UpdatedAt = now;
                session.ConcurrencyToken = Guid.NewGuid();
                await RemovePlaceholderAsync(session, cancellationToken);
            }
            catch (Exception error)
            {
                try
                {
                    var completedObject = await objects.HeadAsync(
                        session.StorageProfileId,
                        session.FileId.ToString(),
                        cancellationToken);
                    if (completedObject is not null)
                    {
                        logger.LogWarning(
                            error,
                            "Multipart upload-сессии {SessionId} уже завершён; удаляем готовый объект и артефакты",
                            session.Id);
                        await cleaner.CleanupAsync(session, cancellationToken);
                        continue;
                    }
                }
                catch (Exception cleanupError)
                {
                    logger.LogWarning(
                        cleanupError,
                        "Не удалось проверить или удалить завершённый объект upload-сессии {SessionId}",
                        session.Id);
                }

                logger.LogWarning(error, "Cleanup upload-сессии {SessionId} снова не выполнен", session.Id);
            }
        }
        if (pending.Count > 0)
            await context.SaveChangesAsync(cancellationToken);

        var retentionBoundary = now.Subtract(TerminalRetention);
        var retained = await context.UploadSessions
            .Where(x => !x.CleanupPending
                        && x.UpdatedAt < retentionBoundary
                        && (x.Status == UploadSessionStatus.Ready
                            || x.Status == UploadSessionStatus.Failed
                            || x.Status == UploadSessionStatus.Cancelled
                            || x.Status == UploadSessionStatus.Expired))
            .Take(500)
            .ToListAsync(cancellationToken);
        if (retained.Count > 0)
        {
            context.UploadSessions.RemoveRange(retained);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task RemovePlaceholderAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        var placeholder = await context.UploadedFiles
            .FirstOrDefaultAsync(x => x.Id == session.FileId && x.UploadedAt == null, cancellationToken);
        if (placeholder is not null)
            context.UploadedFiles.Remove(placeholder);
    }
}
