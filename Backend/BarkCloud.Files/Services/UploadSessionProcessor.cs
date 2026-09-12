using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

using System.Diagnostics;

namespace BarkCloud.Files.Services;

public enum UploadProcessingOutcome
{
    Ready,
    Failed,
    NoOp
}

public sealed class UploadSessionProcessor
{
    public const int MaximumAttempts = 5;

    private readonly FilesContext _context;
    private readonly IUploadEnrichmentPipeline _pipeline;
    private readonly IUploadArtifactCleaner _cleaner;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadSessionProcessor> _logger;

    public UploadSessionProcessor(
        FilesContext context,
        IUploadEnrichmentPipeline pipeline,
        IUploadArtifactCleaner cleaner,
        TimeProvider time,
        ILogger<UploadSessionProcessor> logger)
    {
        _context = context;
        _pipeline = pipeline;
        _cleaner = cleaner;
        _time = time;
        _logger = logger;
    }

    public async Task<UploadProcessingOutcome> ProcessAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var session = await _context.UploadSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("Задача обработки ссылается на отсутствующую upload-сессию {SessionId}", sessionId);
            LogOutcome(sessionId, null, null, UploadProcessingOutcome.NoOp, started);
            return UploadProcessingOutcome.NoOp;
        }
        if (session.Status is UploadSessionStatus.Ready or UploadSessionStatus.Failed
            or UploadSessionStatus.Cancelled or UploadSessionStatus.Expired)
        {
            LogOutcome(session.Id, session.FileId, session.OwnerId, UploadProcessingOutcome.NoOp, started);
            return UploadProcessingOutcome.NoOp;
        }
        if (session.Status != UploadSessionStatus.Processing)
            throw new InvalidOperationException($"Upload-сессия {sessionId} не готова к обработке.");

        session.ProcessingAttempts++;
        session.UpdatedAt = UtcNow();
        session.ConcurrencyToken = Guid.NewGuid();

        UploadFile file;
        try
        {
            await _pipeline.ProcessAsync(session, cancellationToken);
            file = await _context.UploadedFiles
                .FirstOrDefaultAsync(x => x.Id == session.FileId, cancellationToken)
                ?? throw new FileIntegrityException($"Placeholder файла {session.FileId} не найден.");
            if (string.IsNullOrWhiteSpace(file.Etag) || file.Size != session.DeclaredSize)
                throw new FileIntegrityException("Обработанный оригинал не соответствует upload-сессии.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileIntegrityException error)
        {
            await FailAsync(session, "integrity_mismatch", error.Message, cancellationToken);
            LogOutcome(session.Id, session.FileId, session.OwnerId, UploadProcessingOutcome.Failed, started);
            return UploadProcessingOutcome.Failed;
        }
        catch (Exception error) when (session.ProcessingAttempts >= MaximumAttempts)
        {
            _logger.LogError(
                error,
                "Обработка upload-сессии {SessionId} исчерпала {MaximumAttempts} попыток",
                session.Id,
                MaximumAttempts);
            await FailAsync(
                session,
                "processing_retries_exhausted",
                "Не удалось обработать файл после нескольких попыток.",
                cancellationToken);
            LogOutcome(session.Id, session.FileId, session.OwnerId, UploadProcessingOutcome.Failed, started);
            return UploadProcessingOutcome.Failed;
        }
        catch (Exception error)
        {
            session.ErrorCode = "processing_retry";
            session.ErrorMessage = "Временная ошибка обработки файла; выполняется повторная попытка.";
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                error,
                "Обработка upload-сессии {SessionId}, файла {FileId}, пользователя {OwnerId} завершилась инфраструктурной ошибкой, попытка {Attempt}/{MaximumAttempts}",
                session.Id,
                session.FileId,
                session.OwnerId,
                session.ProcessingAttempts,
                MaximumAttempts);
            LogOutcome(session.Id, session.FileId, session.OwnerId, "Retry", started);
            throw;
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        file.UploadedAt = UtcNow();
        session.Status = UploadSessionStatus.Ready;
        session.ReservedBytes = 0;
        session.ErrorCode = null;
        session.ErrorMessage = null;
        session.UpdatedAt = UtcNow();
        session.ConcurrencyToken = Guid.NewGuid();
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Upload-сессия {SessionId}, файл {FileId}, пользователь {OwnerId} готова после {Attempts} попыток",
            session.Id,
            session.FileId,
            session.OwnerId,
            session.ProcessingAttempts);
        LogOutcome(session.Id, session.FileId, session.OwnerId, UploadProcessingOutcome.Ready, started);
        return UploadProcessingOutcome.Ready;
    }

    private void LogOutcome(
        Guid sessionId,
        Guid? fileId,
        long? ownerId,
        object outcome,
        long started)
    {
        _logger.LogInformation(
            "Завершён проход обработки upload-сессии {SessionId}, файла {FileId}, пользователя {OwnerId} с исходом {Outcome} за {ElapsedMs} мс",
            sessionId,
            fileId,
            ownerId,
            outcome,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task FailAsync(
        UploadSession session,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        session.Status = UploadSessionStatus.Failed;
        session.ReservedBytes = 0;
        session.UploadTokenHash = string.Empty;
        session.ErrorCode = errorCode;
        session.ErrorMessage = Truncate(message);
        session.CleanupPending = true;
        session.UpdatedAt = UtcNow();
        session.ConcurrencyToken = Guid.NewGuid();
        await _context.SaveChangesAsync(cancellationToken);
        try
        {
            await _cleaner.CleanupAsync(session, cancellationToken);
        }
        catch (Exception cleanupError)
        {
            _logger.LogWarning(
                cleanupError,
                "Cleanup failed upload-сессии {SessionId}, файла {FileId}, пользователя {OwnerId} будет повторён maintenance",
                session.Id,
                session.FileId,
                session.OwnerId);
        }

        _logger.LogError(
            "Upload-сессия {SessionId}, файл {FileId}, пользователь {OwnerId} завершена с ошибкой {ErrorCode}",
            session.Id,
            session.FileId,
            session.OwnerId,
            errorCode);
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private static string Truncate(string message) =>
        message.Length <= 1024 ? message : message[..1024];
}
