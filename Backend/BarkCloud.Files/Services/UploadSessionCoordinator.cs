using System.Security.Cryptography;
using System.Text;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

public sealed record CreateUploadSessionInput(
    string IdempotencyKey,
    string FileName,
    long FileSize,
    string ContentType,
    string Sha256);

public sealed record UploadSessionResult(
    Guid SessionId,
    Guid FileId,
    UploadSessionStatus Status,
    long FileSize,
    long PartSize,
    DateTime ExpiresAt,
    string? UploadToken,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<MultipartUploadPart> UploadedParts);

public sealed class UploadSessionCoordinator
{
    public const long BasePartSize = 16L * 1024 * 1024;
    public const int MaximumParts = 10_000;
    public const long MaximumObjectSize = 5L * 1024 * 1024 * 1024 * 1024;
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromHours(24);

    private readonly FilesContext _context;
    private readonly IStorageQuotaService _quota;
    private readonly IMultipartUploadStore _objects;
    private readonly IUploadProcessingPublisher _processing;
    private readonly S3BucketRegistry _profiles;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadSessionCoordinator> _logger;

    public UploadSessionCoordinator(
        FilesContext context,
        IStorageQuotaService quota,
        IMultipartUploadStore objects,
        IUploadProcessingPublisher processing,
        S3BucketRegistry profiles,
        TimeProvider time,
        ILogger<UploadSessionCoordinator> logger)
    {
        _context = context;
        _quota = quota;
        _objects = objects;
        _processing = processing;
        _profiles = profiles;
        _time = time;
        _logger = logger;
    }

    public async Task<UploadSessionResult> CreateAsync(
        long ownerId,
        string? deviceName,
        CreateUploadSessionInput input,
        CancellationToken cancellationToken)
    {
        var descriptor = Normalize(input);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var quota = await _quota.GetSnapshotAsync(ownerId, acquireTransactionLock: true, cancellationToken);

        var existing = await _context.UploadSessions
            .FirstOrDefaultAsync(
                x => x.OwnerId == ownerId && x.IdempotencyKey == descriptor.IdempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            if (!Matches(existing, descriptor))
                throw new UploadIdempotencyConflictException();

            string? repeatedToken = null;
            if (existing.Status == UploadSessionStatus.Uploading)
            {
                repeatedToken = GenerateToken();
                existing.UploadTokenHash = HashToken(repeatedToken);
                existing.UpdatedAt = UtcNow();
                existing.ConcurrencyToken = Guid.NewGuid();
                await _context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return Map(existing, repeatedToken);
        }

        quota.EnsureCanReserve(descriptor.FileSize);

        var now = UtcNow();
        var sessionId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var mediaKind = descriptor.FileName.GetMediaKind();
        var profileId = _profiles.ResolveWriteProfileId(UploadFileType.CloudFile, mediaKind, isPreview: false);
        var uploadToken = GenerateToken();
        string? multipartUploadId = null;

        try
        {
            multipartUploadId = await _objects.InitiateAsync(
                profileId,
                fileId.ToString(),
                descriptor.ContentType == "application/octet-stream"
                    ? descriptor.FileName.GetContentType()
                    : descriptor.ContentType,
                descriptor.FileName,
                cancellationToken);

            var session = new UploadSession
            {
                Id = sessionId,
                FileId = fileId,
                OwnerId = ownerId,
                IdempotencyKey = descriptor.IdempotencyKey,
                FileName = descriptor.FileName,
                DeclaredSize = descriptor.FileSize,
                ContentType = descriptor.ContentType,
                Sha256 = descriptor.Sha256,
                Status = UploadSessionStatus.Uploading,
                StorageProfileId = profileId,
                MultipartUploadId = multipartUploadId,
                PartSize = CalculatePartSize(descriptor.FileSize),
                UploadTokenHash = HashToken(uploadToken),
                ReservedBytes = descriptor.FileSize,
                CreatedAt = now,
                UpdatedAt = now,
                LastActivityAt = now,
                ExpiresAt = now.Add(InactivityTimeout),
                ConcurrencyToken = Guid.NewGuid()
            };
            _context.UploadSessions.Add(session);
            _context.UploadedFiles.Add(new UploadFile
            {
                Id = fileId,
                Uploaders = [ownerId],
                CreatedAt = now,
                Type = UploadFileType.CloudFile,
                StorageProfileId = profileId,
                MediaKind = MediaKind.Other,
                Filename = descriptor.FileName,
                UploadDeviceName = deviceName
            });
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Создана upload-сессия {SessionId} для файла {FileId}, пользователь {OwnerId}, профиль {StorageProfileId}, размер {FileSize}",
                sessionId, fileId, ownerId, profileId, descriptor.FileSize);
            return Map(session, uploadToken);
        }
        catch
        {
            if (!string.IsNullOrEmpty(multipartUploadId))
            {
                try
                {
                    await _objects.AbortAsync(
                        profileId, fileId.ToString(), multipartUploadId, CancellationToken.None);
                }
                catch (Exception abortError)
                {
                    _logger.LogWarning(abortError, "Не удалось отменить незарегистрированный multipart {UploadId}", multipartUploadId);
                }
            }
            throw;
        }
    }

    public async Task<UploadSessionResult> GetAsync(
        long ownerId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await FindOwnedAsync(ownerId, sessionId, cancellationToken);
        return Map(session, null);
    }

    public async Task<UploadSessionResult> ResumeAsync(
        long ownerId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await FindOwnedAsync(ownerId, sessionId, cancellationToken);
        if (session.Status != UploadSessionStatus.Uploading)
            return Map(session, null);

        IReadOnlyList<MultipartUploadPart> parts;
        try
        {
            parts = await _objects.ListPartsAsync(
                session.StorageProfileId,
                session.FileId.ToString(),
                session.MultipartUploadId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var stored = await _objects.HeadAsync(
                session.StorageProfileId,
                session.FileId.ToString(),
                cancellationToken);
            if (stored is null || stored.Size != session.DeclaredSize)
                throw;

            _logger.LogWarning(
                error,
                "Multipart upload-сессии {SessionId} уже завершён; состояние восстановлено через HeadObject",
                session.Id);
            return await MoveToProcessingAsync(
                session,
                new CompletedMultipartObject(stored.Etag, stored.Size),
                cancellationToken);
        }

        var token = GenerateToken();
        session.UploadTokenHash = HashToken(token);
        session.UpdatedAt = UtcNow();
        session.ConcurrencyToken = Guid.NewGuid();
        await _context.SaveChangesAsync(cancellationToken);

        return Map(session, token, await ReconcilePartsAsync(session, parts, cancellationToken));
    }

    public async Task<MultipartUploadPart> UploadPartAsync(
        Guid sessionId,
        string token,
        int partNumber,
        long rangeStart,
        long rangeEnd,
        long totalSize,
        Stream body,
        long contentLength,
        CancellationToken cancellationToken)
    {
        var session = await _context.UploadSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            ?? throw new UploadSessionNotFoundException();
        if (session.Status != UploadSessionStatus.Uploading)
            throw new UploadSessionStateException();
        if (!TokenMatches(session.UploadTokenHash, token))
            throw new UploadTokenInvalidException();

        var partCount = (session.DeclaredSize + session.PartSize - 1) / session.PartSize;
        var expectedStart = (partNumber - 1L) * session.PartSize;
        var expectedSize = partNumber >= 1 && partNumber <= partCount
            ? Math.Min(session.PartSize, session.DeclaredSize - expectedStart)
            : -1;
        if (expectedSize <= 0
            || totalSize != session.DeclaredSize
            || rangeStart != expectedStart
            || rangeEnd != expectedStart + expectedSize - 1
            || contentLength != expectedSize)
        {
            throw new UploadPartInvalidException();
        }

        var part = await _objects.UploadPartAsync(
            session.StorageProfileId,
            session.FileId.ToString(),
            session.MultipartUploadId,
            partNumber,
            body,
            expectedSize,
            cancellationToken);

        var now = UtcNow();
        var recordedPart = await _context.UploadSessionParts
            .FirstOrDefaultAsync(
                x => x.SessionId == session.Id && x.PartNumber == part.PartNumber,
                cancellationToken);
        if (recordedPart is null)
        {
            _context.UploadSessionParts.Add(new UploadSessionPart
            {
                SessionId = session.Id,
                PartNumber = part.PartNumber,
                Size = part.Size,
                Etag = part.Etag ?? string.Empty,
                UpdatedAt = now
            });
        }
        else
        {
            recordedPart.Size = part.Size;
            recordedPart.Etag = part.Etag ?? string.Empty;
            recordedPart.UpdatedAt = now;
        }
        session.LastActivityAt = now;
        session.UpdatedAt = now;
        session.ExpiresAt = now.Add(InactivityTimeout);
        session.ConcurrencyToken = Guid.NewGuid();
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Принята часть upload-сессии {SessionId}, файла {FileId}, пользователя {OwnerId}: часть {PartNumber}, размер {PartSize}, ETag подтверждён {HasEtag}",
            session.Id,
            session.FileId,
            session.OwnerId,
            part.PartNumber,
            part.Size,
            !string.IsNullOrWhiteSpace(part.Etag));
        return part;
    }

    public async Task<UploadSessionResult> CompleteAsync(
        long ownerId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await FindOwnedAsync(ownerId, sessionId, cancellationToken);
        if (session.Status != UploadSessionStatus.Uploading)
            return Map(session, null);

        var completed = await CompleteMultipartOrRecoverAsync(session, cancellationToken);
        return await MoveToProcessingAsync(session, completed, cancellationToken);
    }

    public async Task<UploadSessionResult> CancelAsync(
        long ownerId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await FindOwnedAsync(ownerId, sessionId, cancellationToken);
        if (session.Status is UploadSessionStatus.Cancelled or UploadSessionStatus.Expired)
            return Map(session, null);
        if (session.Status != UploadSessionStatus.Uploading)
            throw new UploadSessionStateException();

        try
        {
            await _objects.AbortAsync(
                session.StorageProfileId,
                session.FileId.ToString(),
                session.MultipartUploadId,
                cancellationToken);
        }
        catch (Exception error)
        {
            session.CleanupPending = true;
            _logger.LogWarning(error, "Отмена multipart upload-сессии {SessionId} будет повторена", session.Id);
        }

        session.Status = UploadSessionStatus.Cancelled;
        session.ReservedBytes = 0;
        session.UploadTokenHash = string.Empty;
        session.UpdatedAt = UtcNow();
        session.ConcurrencyToken = Guid.NewGuid();
        if (!session.CleanupPending)
        {
            var placeholder = await _context.UploadedFiles
                .FirstOrDefaultAsync(x => x.Id == session.FileId && x.UploadedAt == null, cancellationToken);
            if (placeholder is not null)
                _context.UploadedFiles.Remove(placeholder);
        }
        await _context.SaveChangesAsync(cancellationToken);

        return Map(session, null);
    }

    public static long CalculatePartSize(long fileSize)
    {
        if (fileSize <= 0 || fileSize > MaximumObjectSize)
            throw new ArgumentOutOfRangeException(nameof(fileSize));
        var required = DivideRoundUp(fileSize, MaximumParts);
        const long mebibyte = 1024 * 1024;
        var roundedMiB = DivideRoundUp(required, mebibyte) * mebibyte;
        return Math.Max(BasePartSize, roundedMiB);
    }

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool TokenMatches(string expectedHash, string token)
    {
        var actualHash = HashToken(token ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expectedHash),
            Convert.FromHexString(actualHash));
    }

    private static CreateUploadSessionInput Normalize(CreateUploadSessionInput input)
    {
        var key = (input.IdempotencyKey ?? string.Empty).Trim();
        var name = Path.GetFileName((input.FileName ?? string.Empty).Trim());
        var contentType = string.IsNullOrWhiteSpace(input.ContentType)
            ? "application/octet-stream"
            : input.ContentType.Trim().ToLowerInvariant();
        var sha256 = (input.Sha256 ?? string.Empty).Trim().ToLowerInvariant();

        if (key.Length is 0 or > 128)
            throw new InvalidUploadSessionRequestException();
        if (name.Length is 0 or > 512)
            throw new InvalidUploadSessionRequestException();
        if (input.FileSize <= 0 || input.FileSize > MaximumObjectSize)
            throw new InvalidUploadSessionRequestException();
        if (contentType.Length > 255)
            throw new InvalidUploadSessionRequestException();
        if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidUploadSessionRequestException();

        return new CreateUploadSessionInput(key, name, input.FileSize, contentType, sha256);
    }

    private static bool Matches(UploadSession session, CreateUploadSessionInput input) =>
        session.FileName == input.FileName
        && session.DeclaredSize == input.FileSize
        && session.ContentType == input.ContentType
        && session.Sha256 == input.Sha256;

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private static long DivideRoundUp(long value, long divisor) =>
        value / divisor + (value % divisor == 0 ? 0 : 1);

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private async Task<UploadSession> FindOwnedAsync(
        long ownerId,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await _context.UploadSessions.FirstOrDefaultAsync(
            x => x.Id == sessionId && x.OwnerId == ownerId,
            cancellationToken) ?? throw new UploadSessionNotFoundException();

    private static void ValidateCompleteParts(UploadSession session, IReadOnlyList<MultipartUploadPart> parts)
    {
        var expectedCount = (int)((session.DeclaredSize + session.PartSize - 1) / session.PartSize);
        if (parts.Count != expectedCount)
            throw new UploadPartsIncompleteException();

        long total = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var expectedNumber = index + 1;
            var expectedSize = Math.Min(session.PartSize, session.DeclaredSize - total);
            var part = parts[index];
            if (part.PartNumber != expectedNumber || part.Size != expectedSize || string.IsNullOrWhiteSpace(part.Etag))
                throw new UploadPartsIncompleteException();
            total += part.Size;
        }
        if (total != session.DeclaredSize)
            throw new UploadPartsIncompleteException();
    }

    private async Task<CompletedMultipartObject> CompleteMultipartOrRecoverAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var listedParts = (await _objects.ListPartsAsync(
                    session.StorageProfileId,
                    session.FileId.ToString(),
                    session.MultipartUploadId,
                    cancellationToken))
                .OrderBy(x => x.PartNumber)
                .ToArray();
            var parts = await ReconcilePartsAsync(session, listedParts, cancellationToken);
            try
            {
                ValidateCompleteParts(session, parts);
            }
            catch (UploadPartsIncompleteException)
            {
                var stored = await TryGetCompletedObjectAsync(session, cancellationToken);
                if (stored is not null && stored.Size == session.DeclaredSize
                    && !string.IsNullOrWhiteSpace(stored.Etag))
                {
                    _logger.LogWarning(
                        "Multipart upload-сессии {SessionId} уже завершён; состояние восстановлено через HeadObject после неполного ListParts",
                        session.Id);
                    return new CompletedMultipartObject(stored.Etag, stored.Size);
                }

                _logger.LogWarning(
                    "Multipart upload-сессии {SessionId}, файла {FileId}, пользователя {OwnerId}, профиль {StorageProfileId} не готов к Complete: частей {ActualPartCount}/{ExpectedPartCount}; детали {Parts}",
                    session.Id,
                    session.FileId,
                    session.OwnerId,
                    session.StorageProfileId,
                    listedParts.Length,
                    ExpectedPartCount(session),
                    string.Join(",", listedParts.Select(static part =>
                        $"{part.PartNumber}:{part.Size}:{(string.IsNullOrWhiteSpace(part.Etag) ? "no-etag" : "etag")}")));
                throw;
            }

            return await _objects.CompleteAsync(
                session.StorageProfileId,
                session.FileId.ToString(),
                session.MultipartUploadId,
                parts,
                cancellationToken);
        }
        catch (UploadPartsIncompleteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var stored = await _objects.HeadAsync(
                session.StorageProfileId,
                session.FileId.ToString(),
                cancellationToken);
            if (stored is null || stored.Size != session.DeclaredSize)
                throw;

            _logger.LogWarning(
                error,
                "Ответ завершения multipart для сессии {SessionId} неоднозначен; объект подтверждён через HeadObject",
                session.Id);
            return new CompletedMultipartObject(stored.Etag, stored.Size);
        }
    }

    private async Task<MultipartObjectInfo?> TryGetCompletedObjectAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _objects.HeadAsync(
                session.StorageProfileId,
                session.FileId.ToString(),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogDebug(
                error,
                "Не удалось проверить готовый объект upload-сессии {SessionId} после неполного ListParts",
                session.Id);
            return null;
        }
    }

    private static long ExpectedPartCount(UploadSession session) =>
        (session.DeclaredSize + session.PartSize - 1) / session.PartSize;

    private async Task<IReadOnlyList<MultipartUploadPart>> ReconcilePartsAsync(
        UploadSession session,
        IReadOnlyList<MultipartUploadPart> listedParts,
        CancellationToken cancellationToken)
    {
        var recordedParts = await _context.UploadSessionParts
            .AsNoTracking()
            .Where(x => x.SessionId == session.Id)
            .OrderBy(x => x.PartNumber)
            .Select(x => new MultipartUploadPart(x.PartNumber, x.Size, x.Etag))
            .ToArrayAsync(cancellationToken);
        if (recordedParts.Length == 0)
            return listedParts;

        // ListParts is normally authoritative. Some S3-compatible gateways have returned
        // an empty list or a part without ETag immediately after a successful UploadPart;
        // the Files response from that upload is persisted, so we can safely repair only
        // those two provider anomalies without trusting browser state.
        if (listedParts.Count == 0)
        {
            _logger.LogWarning(
                "ListParts вернул 0 частей для upload-сессии {SessionId}, файла {FileId}, профиль {StorageProfileId}; используется {RecordedPartCount} подтверждённых Files частей",
                session.Id,
                session.FileId,
                session.StorageProfileId,
                recordedParts.Length);
            return recordedParts;
        }

        var recordedByNumber = recordedParts.ToDictionary(x => x.PartNumber);
        return listedParts
            .Select(part =>
                string.IsNullOrWhiteSpace(part.Etag)
                    && recordedByNumber.TryGetValue(part.PartNumber, out var recorded)
                    && recorded.Size == part.Size
                    && !string.IsNullOrWhiteSpace(recorded.Etag)
                    ? recorded
                    : part)
            .ToArray();
    }

    private async Task<UploadSessionResult> MoveToProcessingAsync(
        UploadSession session,
        CompletedMultipartObject completed,
        CancellationToken cancellationToken)
    {
        if (completed.Size != session.DeclaredSize || string.IsNullOrWhiteSpace(completed.Etag))
            throw new UploadPartsIncompleteException();

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            session.CompletedEtag = completed.Etag;
            session.Status = UploadSessionStatus.Processing;
            session.UploadTokenHash = string.Empty;
            session.UpdatedAt = UtcNow();
            session.ConcurrencyToken = Guid.NewGuid();
            await _processing.PublishAsync(session.Id, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Map(session, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            var current = await FindOwnedAsync(
                session.OwnerId, session.Id, cancellationToken);
            if (current.Status != UploadSessionStatus.Uploading)
                return Map(current, null);
            throw;
        }
    }

    private static UploadSessionResult Map(
        UploadSession session,
        string? token,
        IReadOnlyList<MultipartUploadPart>? parts = null) => new(
        session.Id,
        session.FileId,
        session.Status,
        session.DeclaredSize,
        session.PartSize,
        session.ExpiresAt,
        token,
        session.ErrorCode,
        session.ErrorMessage,
        parts ?? Array.Empty<MultipartUploadPart>());
}
