using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

public readonly record struct LegacyUploadReservation(Guid SessionId, bool RequiresProcessing);

public sealed class LegacyUploadQuotaGuard(
    FilesContext context,
    IStorageQuotaService quota,
    TimeProvider time) : ILegacyUploadCompletionMarker
{
    public async Task<LegacyUploadReservation> ReserveAsync(
        Guid fileId,
        string fileName,
        long fileSize,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var file = await context.UploadedFiles
            .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken)
            ?? throw new BarkCloud.Shared.Exceptions.Files.FileNotFoundException();
        var existing = await context.UploadSessions
            .FirstOrDefaultAsync(x => x.FileId == fileId, cancellationToken);
        if (existing is not null)
        {
            if (existing.DeclaredSize != fileSize
                || existing.FileName != Path.GetFileName(fileName))
            {
                throw new FileAlreadyUploadedException("Upload для этого файла уже завершён");
            }

            if (existing.Status == UploadSessionStatus.Ready && file.IsReady())
            {
                await transaction.CommitAsync(cancellationToken);
                return new LegacyUploadReservation(existing.Id, RequiresProcessing: false);
            }

            if (existing.Status == UploadSessionStatus.Processing)
            {
                if (existing.LegacyProcessingCompletedAt.HasValue)
                {
                    if (string.IsNullOrEmpty(file.Etag) || file.Size != existing.DeclaredSize)
                        throw new FileAlreadyUploadedException("Сохранённый файл не совпадает с заявленным размером");

                    MarkReady(existing, file);
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new LegacyUploadReservation(existing.Id, RequiresProcessing: false);
                }

                throw new FileAlreadyUploadedException("Upload для этого файла уже выполняется");
            }

            throw new FileAlreadyUploadedException("Upload для этого файла уже завершён");
        }

        if (file.IsReady())
            throw new FileAlreadyUploadedException("Файл уже был загружен");

        var snapshot = await quota.GetSnapshotAsync(
            file.Uploaders.FirstOrDefault(), acquireTransactionLock: true, cancellationToken);
        snapshot.EnsureCanReserve(fileSize);

        var now = time.GetUtcNow().UtcDateTime;
        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            FileId = fileId,
            OwnerId = file.Uploaders.FirstOrDefault(),
            IdempotencyKey = $"legacy:{fileId:N}",
            FileName = Path.GetFileName(fileName),
            DeclaredSize = fileSize,
            ContentType = fileName.GetContentType(),
            Sha256 = string.Empty,
            Status = UploadSessionStatus.Processing,
            StorageProfileId = file.StorageProfileId,
            MultipartUploadId = string.Empty,
            PartSize = fileSize,
            UploadTokenHash = string.Empty,
            ReservedBytes = fileSize,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now,
            ExpiresAt = now.Add(UploadSessionCoordinator.InactivityTimeout),
            ConcurrencyToken = Guid.NewGuid()
        };
        context.UploadSessions.Add(session);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LegacyUploadReservation(session.Id, RequiresProcessing: true);
    }

    public async Task MarkProcessingCompletedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await context.UploadSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is null || session.Status == UploadSessionStatus.Ready)
            return;
        if (session.Status != UploadSessionStatus.Processing)
            throw new InvalidOperationException("Legacy upload reservation is not active.");

        var file = await context.UploadedFiles
            .FirstOrDefaultAsync(x => x.Id == session.FileId, cancellationToken);
        if (file is null
            || string.IsNullOrEmpty(file.Etag)
            || file.Size != session.DeclaredSize)
        {
            throw new InvalidOperationException("Legacy upload did not persist the declared original.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        session.LegacyProcessingCompletedAt ??= now;
        session.UpdatedAt = now;
        session.ConcurrencyToken = Guid.NewGuid();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var session = await context.UploadSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is null || session.Status == UploadSessionStatus.Ready)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (session.Status != UploadSessionStatus.Processing)
            throw new InvalidOperationException("Legacy upload reservation is not active.");
        if (!session.LegacyProcessingCompletedAt.HasValue)
            throw new InvalidOperationException("Legacy upload processing is not complete.");

        var file = await context.UploadedFiles
            .FirstOrDefaultAsync(x => x.Id == session.FileId, cancellationToken);
        if (file is null
            || string.IsNullOrEmpty(file.Etag)
            || file.Size != session.DeclaredSize)
        {
            throw new InvalidOperationException("Legacy upload did not persist the declared original.");
        }

        MarkReady(session, file);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void MarkReady(UploadSession session, UploadFile file)
    {
        var now = time.GetUtcNow().UtcDateTime;
        file.UploadedAt ??= now;
        session.Status = UploadSessionStatus.Ready;
        session.ReservedBytes = 0;
        session.ErrorCode = null;
        session.ErrorMessage = null;
        session.UpdatedAt = now;
        session.ConcurrencyToken = Guid.NewGuid();
    }

    public async Task FailAsync(Guid sessionId, Exception error, CancellationToken cancellationToken)
    {
        var session = context.UploadSessions.Local.FirstOrDefault(x => x.Id == sessionId)
                      ?? await context.UploadSessions.FirstOrDefaultAsync(
                          x => x.Id == sessionId, cancellationToken);
        if (session?.LegacyProcessingCompletedAt.HasValue == true)
        {
            await CompleteAsync(sessionId, cancellationToken);
            return;
        }

        await FinishAsync(sessionId, UploadSessionStatus.Failed, error.Message, cancellationToken);
    }

    private async Task FinishAsync(
        Guid sessionId,
        UploadSessionStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        var session = await context.UploadSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is null || session.Status is UploadSessionStatus.Ready or UploadSessionStatus.Failed)
            return;

        session.Status = status;
        session.ReservedBytes = 0;
        session.ErrorCode = error is null ? null : "legacy_upload_failed";
        session.ErrorMessage = error is { Length: > 1024 } ? error[..1024] : error;
        session.CleanupPending = true;
        session.UpdatedAt = time.GetUtcNow().UtcDateTime;
        session.ConcurrencyToken = Guid.NewGuid();
        await context.SaveChangesAsync(cancellationToken);
    }
}
