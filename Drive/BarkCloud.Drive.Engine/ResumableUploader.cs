using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Proto.Files;

using Grpc.Core;

namespace BarkCloud.Drive.Engine;

internal sealed class UploadProcessingPendingException() : Exception(Loc.T("Eng_UploadProcessingPending"));

internal sealed class ResumableUploader
{
    private static readonly TimeSpan InitialPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartLimitCooldown = TimeSpan.FromHours(1);

    private readonly FilesApi.FilesApiClient _files;
    private readonly HttpClient _http;
    private readonly string? _uploadOrigin;
    private readonly TimeProvider _time;
    internal Action<TimeSpan>? ProcessingWaitStartedForTesting { get; set; }

    public ResumableUploader(FilesApi.FilesApiClient files, HttpClient http, string? uploadOrigin,
        TimeProvider time)
    {
        _files = files;
        _http = http;
        _uploadOrigin = uploadOrigin?.TrimEnd('/');
        _time = time;
    }

    public async Task<string> UploadAsync(PersistOperation operation, string localPath, string name,
        Action saveState, TimeSpan? processingWaitLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_uploadOrigin))
            throw new InvalidOperationException(Loc.T("Eng_UploadOriginMissing"));
        var state = operation.Upload2;
        if (state is null)
        {
            state = await CreateStateAsync(localPath, name, cancellationToken).ConfigureAwait(false);
            operation.Upload2 = state;
            saveState();
        }

        EnsureRestartAllowed(state, saveState);
        var resumeAttempts = 0;
        void CountResume(UploadPartRequestException exception)
        {
            if (resumeAttempts >= 3)
                throw new InvalidOperationException(Loc.T("Eng_UploadResumeLimitFmt", exception.Message), exception);
            resumeAttempts++;
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UploadSessionResponse session;
            try
            {
                session = state.SessionId is null
                    ? CreateSession(operation, state, saveState, cancellationToken)
                    : GetOrResumeSession(state, cancellationToken);
            }
            catch (UploadSessionMissingException)
            {
                RestartSession(operation, state, saveState);
                continue;
            }

            UpdateState(operation, state, session, saveState);

            if (session.Status == UploadSessionStatus.Uploading)
            {
                try
                {
                    session = await UploadPartsAndCompleteAsync(operation, state, localPath, saveState,
                        CountResume, cancellationToken).ConfigureAwait(false);
                }
                catch (UploadSessionMissingException)
                {
                    RestartSession(operation, state, saveState);
                    continue;
                }
            }

            if (session.Status == UploadSessionStatus.Processing)
            {
                if (operation.Stage != PersistStage.Processing)
                {
                    operation.Stage = PersistStage.Processing;
                    saveState();
                }
                session = await WaitForProcessingAsync(operation, state, saveState,
                    processingWaitLimit, cancellationToken).ConfigureAwait(false);
            }

            if (session.Status == UploadSessionStatus.Ready)
            {
                if (string.IsNullOrWhiteSpace(session.FileId))
                    throw new InvalidDataException(Loc.T("Eng_UploadSessionInvalidResponse"));

                return session.FileId;
            }

            if (IsTerminal(session.Status))
            {
                RestartSession(operation, state, saveState);
                continue;
            }

            if (session.Status != UploadSessionStatus.Uploading)
                throw new InvalidDataException(Loc.T("Eng_UploadSessionInvalidResponse"));
        }
    }

    private static async Task<Upload2State> CreateStateAsync(string localPath, string name,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new Upload2State
        {
            IdempotencyKey = Guid.NewGuid().ToString("D"),
            Sha256 = Convert.ToHexString(hash),
            Size = size,
            ContentType = "application/octet-stream",
            Name = name,
        };
    }

    private UploadSessionResponse CreateSession(PersistOperation operation, Upload2State state,
        Action saveState, CancellationToken cancellationToken)
    {
        var retriedLostResponse = false;
        while (true)
        {
            try
            {
                var response = _files.CreateUploadSession(new CreateUploadSessionRequest
                {
                    IdempotencyKey = state.IdempotencyKey,
                    FileName = state.Name,
                    FileSize = state.Size,
                    ContentType = state.ContentType,
                    Sha256 = state.Sha256,
                }, cancellationToken: cancellationToken);
                if (string.IsNullOrWhiteSpace(response.SessionId) || string.IsNullOrWhiteSpace(response.FileId))
                    throw new InvalidDataException(Loc.T("Eng_UploadSessionInvalidResponse"));
                return response;
            }
            catch (RpcException ex) when (ServerErrors.HasCode(ex, ServerErrors.UploadIdempotencyConflict))
            {
                RestartSession(operation, state, saveState);
                retriedLostResponse = false;
            }
            catch (RpcException ex) when (!retriedLostResponse && IsLostCreateResponse(ex))
            {
                retriedLostResponse = true;
            }
        }
    }

    private UploadSessionResponse GetOrResumeSession(Upload2State state, CancellationToken cancellationToken)
    {
        var sessionId = state.SessionId!;
        try
        {
            return _files.GetUploadSession(new UploadSessionIdRequest { SessionId = sessionId },
                cancellationToken: cancellationToken);
        }
        catch (RpcException ex) when (ServerErrors.HasCode(ex, ServerErrors.UploadSessionNotFound))
        {
            throw new UploadSessionMissingException(ex);
        }
    }

    private UploadSessionResponse ResumeSession(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            return _files.ResumeUploadSession(new UploadSessionIdRequest { SessionId = sessionId },
                cancellationToken: cancellationToken);
        }
        catch (RpcException ex) when (ServerErrors.HasCode(ex, ServerErrors.UploadSessionNotFound))
        {
            throw new UploadSessionMissingException(ex);
        }
    }

    private async Task<UploadSessionResponse> UploadPartsAndCompleteAsync(PersistOperation operation,
        Upload2State state, string localPath, Action saveState, Action<UploadPartRequestException> countResume,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = ResumeSession(state.SessionId!, cancellationToken);
            UpdateState(operation, state, session, saveState);
            if (session.Status != UploadSessionStatus.Uploading)
                return session;
            if (session.PartSize <= 0 || string.IsNullOrWhiteSpace(session.UploadToken))
                throw new InvalidDataException(Loc.T("Eng_UploadSessionInvalidResponse"));

            var parts = session.UploadedParts.ToDictionary(part => part.PartNumber);
            var partCount = (state.Size - 1) / session.PartSize + 1;
            var resumeParts = false;
            for (var partNumber = 1; partNumber <= partCount; partNumber++)
            {
                if (parts.TryGetValue(partNumber, out var part) && part.HasEtag)
                    continue;

                var start = (long)(partNumber - 1) * session.PartSize;
                var length = Math.Min(session.PartSize, state.Size - start);
                try
                {
                    await UploadPartAsync(state.SessionId!, partNumber, session.UploadToken,
                        localPath, start, length, state.Size, cancellationToken).ConfigureAwait(false);
                }
                catch (UploadPartRequestException ex) when (
                    ex.ErrorCode == ServerErrors.UploadSessionNotFound
                    || ex.ErrorCode == ServerErrors.UploadTokenInvalid
                    || ex.ErrorCode == ServerErrors.UploadSessionState)
                {
                    countResume(ex);
                    resumeParts = true;
                    break;
                }
            }

            if (resumeParts)
                continue;

            var completed = _files.CompleteUploadSession(
                new UploadSessionIdRequest { SessionId = state.SessionId! },
                cancellationToken: cancellationToken);
            UpdateState(operation, state, completed, saveState);
            return completed;
        }
    }

    private async Task UploadPartAsync(string sessionId, int partNumber, string token, string localPath,
        long start, long length, long total, CancellationToken cancellationToken)
    {
        using var idleTimeout = new CloudGateway.TransferIdleTimeout(_time);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, idleTimeout.Token);
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Position = start;
        using var segment = new FileSegmentStream(file, length);
        using var progress = new CloudGateway.ProgressStream(segment,
            idleTimeout.ReportProgress, idleTimeout.WaitForResponse);
        using var content = new StreamContent(progress);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = length;
        content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, total);

        var uri = $"{_uploadOrigin}/file-upload/{Uri.EscapeDataString(sessionId)}/parts/{partNumber}";
        using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Upload-Token", token);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token).ConfigureAwait(false);
            idleTimeout.ReportProgress();
            if (!response.IsSuccessStatusCode)
            {
                var errorCode = await ReadErrorCodeAsync(response, requestCancellation.Token).ConfigureAwait(false);
                if (errorCode is not null)
                    throw new UploadPartRequestException(response.StatusCode, errorCode);
                response.EnsureSuccessStatusCode();
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestCancellation.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(responseStream,
                cancellationToken: requestCancellation.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("partNumber", out var returnedPartNumber)
                || returnedPartNumber.GetInt32() != partNumber
                || !root.TryGetProperty("size", out var returnedSize)
                || returnedSize.GetInt64() != length)
                throw new InvalidDataException(Loc.T("Eng_UploadSessionInvalidResponse"));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                                                   && idleTimeout.Token.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("Eng_HttpTransferTimedOut"), ex);
        }
    }

    private async Task<UploadSessionResponse> WaitForProcessingAsync(PersistOperation operation, Upload2State state,
        Action saveState, TimeSpan? waitLimit, CancellationToken cancellationToken)
    {
        using var timeout = waitLimit is { } limit ? new CancellationTokenSource(limit, _time) : null;
        using var linkedCancellation = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var pollCancellationToken = linkedCancellation?.Token ?? cancellationToken;
        var pollInterval = InitialPollInterval;
        try
        {
            while (true)
            {
                var delay = Task.Delay(pollInterval, _time, pollCancellationToken);
                ProcessingWaitStartedForTesting?.Invoke(pollInterval);
                await delay.ConfigureAwait(false);
                var session = _files.GetUploadSession(new UploadSessionIdRequest { SessionId = state.SessionId! },
                    cancellationToken: pollCancellationToken);
                UpdateState(operation, state, session, saveState);
                if (session.Status != UploadSessionStatus.Processing)
                    return session;

                pollInterval = TimeSpan.FromTicks(Math.Min(pollInterval.Ticks * 2, MaxPollInterval.Ticks));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && timeout?.IsCancellationRequested == true)
        {
            throw new UploadProcessingPendingException();
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested
                                      && timeout?.IsCancellationRequested == true
                                      && ex.StatusCode == StatusCode.Cancelled)
        {
            throw new UploadProcessingPendingException();
        }
    }

    private static void UpdateState(PersistOperation operation, Upload2State state,
        UploadSessionResponse session, Action saveState)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(session.SessionId) && state.SessionId != session.SessionId)
        {
            state.SessionId = session.SessionId;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(session.FileId) && operation.FileId != session.FileId)
        {
            operation.FileId = session.FileId;
            changed = true;
        }
        if (changed)
            saveState();
    }

    private void EnsureRestartAllowed(Upload2State state, Action saveState)
    {
        if (state.SessionRestarts < 3)
            return;

        var limitReachedAt = state.SessionRestartLimitReachedAtUtc;
        if (limitReachedAt is null)
        {
            limitReachedAt = _time.GetUtcNow();
            state.SessionRestartLimitReachedAtUtc = limitReachedAt;
            saveState();
        }

        var elapsed = _time.GetUtcNow() - limitReachedAt.Value;
        if (elapsed >= TimeSpan.Zero && elapsed < RestartLimitCooldown)
            throw new InvalidOperationException(Loc.T("Eng_UploadRestartLimit"));

        state.SessionRestarts = 0;
        state.SessionRestartLimitReachedAtUtc = null;
        saveState();
    }

    private void RestartSession(PersistOperation operation, Upload2State state, Action saveState)
    {
        state.SessionRestarts++;
        state.IdempotencyKey = Guid.NewGuid().ToString("D");
        state.SessionId = null;
        if (state.SessionRestarts >= 3)
            state.SessionRestartLimitReachedAtUtc = _time.GetUtcNow();
        operation.FileId = null;
        operation.Stage = PersistStage.Upload;
        saveState();
        EnsureRestartAllowed(state, saveState);
    }

    private static bool IsTerminal(UploadSessionStatus status)
        => status is UploadSessionStatus.Failed
            or UploadSessionStatus.Cancelled
            or UploadSessionStatus.Expired;

    private static bool IsLostCreateResponse(RpcException exception)
        => exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Unknown;

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class UploadSessionMissingException(Exception innerException)
        : Exception(Loc.T("Eng_UploadSessionMissing"), innerException);

    private sealed class UploadPartRequestException(HttpStatusCode statusCode, string errorCode)
        : Exception($"HTTP {(int)statusCode}: {errorCode}")
    {
        public string ErrorCode { get; } = errorCode;
    }

    private sealed class FileSegmentStream(Stream stream, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = stream.Read(buffer, offset, (int)Math.Min(count, length - _position));
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await stream.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - _position)],
                cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
