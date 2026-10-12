using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using BarkCloud.Drive.Engine;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using ProtoDirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;

namespace BarkCloud.Drive.Engine.Tests.Fakes;

internal static class FakeCloudMethods
{
    public const string GetUploadUrl = "/barkcloud.files.FilesApi/GetUploadUrl";
    public const string CreateUploadSession = "/barkcloud.files.FilesApi/CreateUploadSession";
    public const string GetUploadSession = "/barkcloud.files.FilesApi/GetUploadSession";
    public const string ResumeUploadSession = "/barkcloud.files.FilesApi/ResumeUploadSession";
    public const string CancelUploadSession = "/barkcloud.files.FilesApi/CancelUploadSession";
    public const string CompleteUploadSession = "/barkcloud.files.FilesApi/CompleteUploadSession";
    public const string GetTempDownloadUrl = "/barkcloud.files.FilesApi/GetTempDownloadUrl";
    public const string GetUserStorageInfo = "/barkcloud.files.FilesApi/GetUserStorageInfo";
    public const string ListDirectoryDetailed = "/barkcloud.files.CloudApi/ListDirectoryDetailed";
    public const string CreateDirectory = "/barkcloud.files.CloudApi/CreateDirectory";
    public const string RenameDirectory = "/barkcloud.files.CloudApi/RenameDirectory";
    public const string MoveDirectory = "/barkcloud.files.CloudApi/MoveDirectory";
    public const string DeleteDirectory = "/barkcloud.files.CloudApi/DeleteDirectory";
    public const string AttachFile = "/barkcloud.files.CloudApi/AttachFile";
    public const string RenameFileEntry = "/barkcloud.files.CloudApi/RenameFileEntry";
    public const string MoveFileEntry = "/barkcloud.files.CloudApi/MoveFileEntry";
    public const string DeleteFileEntry = "/barkcloud.files.CloudApi/DeleteFileEntry";
    public const string DeleteFileEntries = "/barkcloud.files.CloudApi/DeleteFileEntries";
    public const string RestoreFromTrash = "/barkcloud.files.CloudApi/RestoreFromTrash";
}

internal sealed class FakeCloud
{
    private const string FileNotFoundCode = "91E25C73-FC80-43C1-893D-F26F39726F03";
    private const string FileEntryNotFoundCode = "8E3A9C12-5F1D-4D32-B7F4-1A2B3C4D5E6F";
    private const string DirectoryNameConflictCode = "C0A4E97C-2E73-4D5D-AB1A-7E3DC8A1D8F1";
    private const string CircularMoveCode = "F2B82A3C-1D2E-4B0E-9A19-2D90F8B7C001";
    private const string DirectoryNotFoundCode = "B86C8B6E-2A12-44E1-9B6B-1AE0F1C0E1AE";
    private const string FileAlreadyAttachedCode = "F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B";

    private readonly object _gate = new();
    private readonly Dictionary<string, CloudDirectory> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CloudEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CloudEntry> _trash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CloudBlob> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeUploadSession> _uploadSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeUploadSession> _uploadSessionsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _operations = new();
    private bool _failedUploadPart;

    public FakeCloud()
    {
        _directories.Add("", new CloudDirectory("", "", "", DateTime.UtcNow));
        HttpHandler = new FakeHttpHandler(async (request, cancellationToken) =>
        {
            _operations.Enqueue($"HTTP:{request.Method.Method}");
            return await HandleHttpAsync(request, cancellationToken);
        });
        Invoker.OnCall = method => _operations.Enqueue($"RPC:{method}");

        Invoker.Handle<ListDirectoryRequest, DirectoryListingDetailed>(
            FakeCloudMethods.ListDirectoryDetailed, ListDirectoryDetailed);
        Invoker.Handle<CreateUploadSessionRequest, UploadSessionResponse>(
            FakeCloudMethods.CreateUploadSession, CreateUploadSession);
        Invoker.Handle<UploadSessionIdRequest, UploadSessionResponse>(
            FakeCloudMethods.GetUploadSession, GetUploadSession);
        Invoker.Handle<UploadSessionIdRequest, UploadSessionResponse>(
            FakeCloudMethods.ResumeUploadSession, ResumeUploadSession);
        Invoker.Handle<UploadSessionIdRequest, UploadSessionResponse>(
            FakeCloudMethods.CompleteUploadSession, CompleteUploadSession);
        Invoker.Handle<UploadSessionIdRequest, UploadSessionResponse>(
            FakeCloudMethods.CancelUploadSession, request =>
            {
                var session = GetUploadSession(request);
                if (session.Status is UploadSessionStatus.Cancelled or UploadSessionStatus.Expired)
                    return session;
                if (session.Status != UploadSessionStatus.Uploading)
                    throw BusinessError(ServerErrors.UploadSessionState);
                SetUploadSessionStatus(request.SessionId, UploadSessionStatus.Cancelled);
                return GetUploadSession(request);
            });
        Invoker.Handle<GetTempDownloadUrlRequest, GetTempDownloadUrlResponse>(
            FakeCloudMethods.GetTempDownloadUrl, GetTempDownloadUrl);
        Invoker.Handle<GetUserStorageInfoRequest, GetUserStorageInfoResponse>(
            FakeCloudMethods.GetUserStorageInfo, _ => new GetUserStorageInfoResponse
            {
                TotalAvailableStorage = 1L << 40,
                DiskUsedStorage = 1L << 30,
                S3UsedStorage = 1L << 30,
            });
        Invoker.Handle<CreateDirectoryRequest, ProtoDirectoryInfo>(FakeCloudMethods.CreateDirectory, CreateDirectory);
        Invoker.Handle<RenameDirectoryRequest, CloudEmpty>(FakeCloudMethods.RenameDirectory, RenameDirectory);
        Invoker.Handle<MoveDirectoryRequest, CloudEmpty>(FakeCloudMethods.MoveDirectory, MoveDirectory);
        Invoker.Handle<DeleteDirectoryRequest, CloudEmpty>(FakeCloudMethods.DeleteDirectory, DeleteDirectory);
        Invoker.Handle<AttachFileRequest, CloudEmpty>(FakeCloudMethods.AttachFile, AttachFile);
        Invoker.Handle<RenameFileEntryRequest, CloudEmpty>(FakeCloudMethods.RenameFileEntry, RenameFileEntry);
        Invoker.Handle<MoveFileEntryRequest, CloudEmpty>(FakeCloudMethods.MoveFileEntry, MoveFileEntry);
        Invoker.Handle<DeleteFileEntryRequest, CloudEmpty>(FakeCloudMethods.DeleteFileEntry, DeleteFileEntry);
        Invoker.Handle<DeleteFileEntriesRequest, DeleteFileEntriesResponse>(
            FakeCloudMethods.DeleteFileEntries, DeleteFileEntries);
        Invoker.Handle<RestoreFromTrashRequest, CloudEmpty>(FakeCloudMethods.RestoreFromTrash, RestoreFromTrash);
    }

    public FakeCallInvoker Invoker { get; } = new();
    public FakeHttpHandler HttpHandler { get; }
    public IReadOnlyList<string> Operations => _operations.ToArray();
    public long UploadPartSize { get; set; } = 3;
    public Queue<UploadSessionStatus> UploadCompleteStatuses { get; } = new();
    public int? FailUploadPartNumberOnce { get; set; }

    public IReadOnlyList<string> UploadSessionIds
    {
        get { lock (_gate) return _uploadSessions.Keys.ToArray(); }
    }

    public CloudGateway CreateGateway(string cacheDir, TimeProvider? time = null,
        long? cacheLimitBytesForTesting = null)
        => new(
            new CloudApi.CloudApiClient(Invoker),
            new FilesApi.FilesApiClient(Invoker),
            new HttpClient(HttpHandler, disposeHandler: false),
            "https://files.test",
            cacheDir,
            time,
            cacheLimitBytesForTesting,
            uploadOrigin: "https://files.test");

    public string AddDirectory(string name, string parentId = "")
    {
        lock (_gate)
        {
            EnsureDirectory(parentId);
            if (DirectoryNameExists(parentId, name))
                throw new InvalidOperationException($"Имя «{name}» уже занято");

            var id = Guid.NewGuid().ToString();
            _directories.Add(id, new CloudDirectory(id, parentId, name, DateTime.UtcNow));
            return id;
        }
    }

    public string AddUnattachedFile(byte[]? content = null)
    {
        lock (_gate)
        {
            var fileId = Guid.NewGuid().ToString();
            _blobs.Add(fileId, new CloudBlob(fileId, content?.ToArray() ?? Array.Empty<byte>()));
            return fileId;
        }
    }

    public FakeCloudFile AddFile(string directoryId, string name, byte[]? content = null)
    {
        lock (_gate)
        {
            EnsureDirectory(directoryId);
            if (FileNameExists(directoryId, name))
                throw new InvalidOperationException($"Имя «{name}» уже занято");

            var fileId = Guid.NewGuid().ToString();
            _blobs.Add(fileId, new CloudBlob(fileId, content?.ToArray() ?? Array.Empty<byte>()));
            var entry = new CloudEntry(Guid.NewGuid().ToString(), directoryId, fileId, name, DateTime.UtcNow);
            _entries.Add(entry.Id, entry);
            return ToReference(entry);
        }
    }

    public bool IsInTrash(string entryId)
    {
        lock (_gate)
            return _trash.ContainsKey(entryId);
    }

    private DirectoryListingDetailed ListDirectoryDetailed(ListDirectoryRequest request)
    {
        lock (_gate)
        {
            var directoryId = request.DirectoryId;
            EnsureDirectory(directoryId);
            var directories = _directories.Values
                .Where(directory => directory.Id.Length > 0 && directory.ParentId == directoryId)
                .OrderBy(directory => directory.Name, StringComparer.Ordinal)
                .ToArray();
            var entries = _entries.Values
                .Where(entry => entry.DirectoryId == directoryId)
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .Where(entry => IsAfterCursor(entry, request.CursorName, request.CursorEntryId))
                .ToArray();
            var limit = request.Limit <= 0 ? 50 : Math.Min(request.Limit, 200);
            var page = entries.Take(limit).ToArray();
            var response = new DirectoryListingDetailed();
            response.Subdirs.Add(directories.Select(ToProto));

            foreach (var entry in page)
            {
                if (!_blobs.TryGetValue(entry.FileId, out var blob))
                    continue;

                response.Files.Add(new FileEntryDetailed
                {
                    Entry = ToProto(entry),
                    File = ToProto(blob),
                });
            }

            if (entries.Length > page.Length && page.Length > 0)
            {
                var last = page[^1];
                response.NextCursorName = last.Name;
                response.NextCursorEntryId = last.Id;
            }

            return response;
        }
    }

    public void SetUploadSessionStatus(string sessionId, UploadSessionStatus status, string errorMessage = "")
    {
        lock (_gate)
        {
            var session = _uploadSessions[sessionId];
            session.Status = status;
            session.ErrorMessage = errorMessage;
            if (status == UploadSessionStatus.Ready && session.CompletedData is not null)
                _blobs[session.FileId] = new CloudBlob(session.FileId, session.CompletedData.ToArray());
        }
    }

    private UploadSessionResponse CreateUploadSession(CreateUploadSessionRequest request)
    {
        lock (_gate)
        {
            if (_uploadSessionsByKey.TryGetValue(request.IdempotencyKey, out var existing))
            {
                if (existing.Name != request.FileName || existing.Size != request.FileSize
                    || existing.ContentType != request.ContentType || existing.Sha256 != request.Sha256)
                    throw BusinessError(ServerErrors.UploadIdempotencyConflict);
                return ToUploadSession(existing, includeToken: true);
            }

            var session = new FakeUploadSession
            {
                SessionId = Guid.NewGuid().ToString("D"),
                FileId = Guid.NewGuid().ToString("D"),
                IdempotencyKey = request.IdempotencyKey,
                Name = request.FileName,
                Size = request.FileSize,
                ContentType = request.ContentType,
                Sha256 = request.Sha256,
                PartSize = UploadPartSize,
                UploadToken = Guid.NewGuid().ToString("N"),
                Status = UploadSessionStatus.Uploading,
            };
            _uploadSessions.Add(session.SessionId, session);
            _uploadSessionsByKey.Add(session.IdempotencyKey, session);
            return ToUploadSession(session, includeToken: true);
        }
    }

    private UploadSessionResponse GetUploadSession(UploadSessionIdRequest request)
    {
        lock (_gate)
        {
            if (!_uploadSessions.TryGetValue(request.SessionId, out var session))
                throw BusinessError(ServerErrors.UploadSessionNotFound);
            return ToUploadSession(session, includeToken: false);
        }
    }

    private UploadSessionResponse ResumeUploadSession(UploadSessionIdRequest request)
    {
        lock (_gate)
        {
            if (!_uploadSessions.TryGetValue(request.SessionId, out var session))
                throw BusinessError(ServerErrors.UploadSessionNotFound);
            if (session.Status == UploadSessionStatus.Uploading)
                session.UploadToken = Guid.NewGuid().ToString("N");
            return ToUploadSession(session, includeToken: true);
        }
    }

    private UploadSessionResponse CompleteUploadSession(UploadSessionIdRequest request)
    {
        lock (_gate)
        {
            if (!_uploadSessions.TryGetValue(request.SessionId, out var session))
                throw BusinessError(ServerErrors.UploadSessionNotFound);
            var partCount = (session.Size - 1) / session.PartSize + 1;
            if (Enumerable.Range(1, checked((int)partCount)).Any(number =>
                    !session.Parts.TryGetValue(number, out var part) || !part.HasEtag))
                return ToUploadSession(session, includeToken: false);

            var data = new byte[checked((int)session.Size)];
            foreach (var part in session.Parts.OrderBy(item => item.Key))
            {
                var start = (long)(part.Key - 1) * session.PartSize;
                Buffer.BlockCopy(part.Value.Data, 0, data, checked((int)start), part.Value.Data.Length);
            }
            session.CompletedData = data;
            session.Status = UploadCompleteStatuses.Count == 0
                ? UploadSessionStatus.Ready
                : UploadCompleteStatuses.Dequeue();
            if (session.Status == UploadSessionStatus.Ready)
                _blobs[session.FileId] = new CloudBlob(session.FileId, data.ToArray());
            return ToUploadSession(session, includeToken: false);
        }
    }

    private static UploadSessionResponse ToUploadSession(FakeUploadSession session, bool includeToken)
    {
        var response = new UploadSessionResponse
        {
            SessionId = session.SessionId,
            FileId = session.FileId,
            Status = session.Status,
            FileSize = session.Size,
            PartSize = session.PartSize,
            UploadToken = includeToken && session.Status == UploadSessionStatus.Uploading
                ? session.UploadToken : "",
            ErrorMessage = session.ErrorMessage,
        };
        response.UploadedParts.Add(session.Parts.OrderBy(part => part.Key).Select(part => new UploadPartInfo
        {
            PartNumber = part.Key,
            Size = part.Value.Data.LongLength,
            HasEtag = part.Value.HasEtag,
        }));
        return response;
    }

    private GetTempDownloadUrlResponse GetTempDownloadUrl(GetTempDownloadUrlRequest request)
    {
        lock (_gate)
        {
            var response = new GetTempDownloadUrlResponse();
            foreach (var fileId in request.FileIds)
            {
                if (_blobs.ContainsKey(fileId))
                    response.FileUrls.Add(new GetTempDownloadUrlResponse.Types.DownloadFileData
                    {
                        FileId = fileId,
                        Url = $"https://files.test/download/{fileId}",
                    });
            }

            return response;
        }
    }

    private ProtoDirectoryInfo CreateDirectory(CreateDirectoryRequest request)
    {
        lock (_gate)
        {
            EnsureDirectory(request.ParentId);
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) || DirectoryNameExists(request.ParentId, name))
                throw BusinessError(DirectoryNameConflictCode);

            var directory = new CloudDirectory(Guid.NewGuid().ToString(), request.ParentId, name, DateTime.UtcNow);
            _directories.Add(directory.Id, directory);
            return ToProto(directory);
        }
    }

    private CloudEmpty RenameDirectory(RenameDirectoryRequest request)
    {
        lock (_gate)
        {
            if (!_directories.TryGetValue(request.DirectoryId, out var directory) || directory.Id.Length == 0)
                throw BusinessError(DirectoryNotFoundCode);
            var name = request.NewName.Trim();
            if (string.IsNullOrWhiteSpace(name) || DirectoryNameExists(directory.ParentId, name, ignoreDirectoryId: directory.Id))
                throw BusinessError(DirectoryNameConflictCode);

            directory.Name = name;
            directory.UpdatedAt = DateTime.UtcNow;
            return new CloudEmpty();
        }
    }

    private CloudEmpty MoveDirectory(MoveDirectoryRequest request)
    {
        lock (_gate)
        {
            if (!_directories.TryGetValue(request.DirectoryId, out var directory) || directory.Id.Length == 0)
                throw BusinessError(DirectoryNotFoundCode);
            EnsureDirectory(request.NewParentId);
            if (IsDescendant(request.NewParentId, directory.Id))
                throw BusinessError(CircularMoveCode);
            if (DirectoryNameExists(request.NewParentId, directory.Name, ignoreDirectoryId: directory.Id))
                throw BusinessError(DirectoryNameConflictCode);

            directory.ParentId = request.NewParentId;
            directory.UpdatedAt = DateTime.UtcNow;
            return new CloudEmpty();
        }
    }

    private CloudEmpty DeleteDirectory(DeleteDirectoryRequest request)
    {
        lock (_gate)
        {
            if (!_directories.TryGetValue(request.DirectoryId, out var directory) || directory.Id.Length == 0)
                throw BusinessError(DirectoryNotFoundCode);

            var descendants = new HashSet<string>(StringComparer.Ordinal) { directory.Id };
            var pending = new Queue<string>();
            pending.Enqueue(directory.Id);
            while (pending.TryDequeue(out var parentId))
            {
                foreach (var child in _directories.Values.Where(item => item.ParentId == parentId).ToArray())
                {
                    descendants.Add(child.Id);
                    pending.Enqueue(child.Id);
                }
            }

            foreach (var entry in _entries.Values.Where(entry => descendants.Contains(entry.DirectoryId)).ToArray())
                MoveToTrash(entry);
            foreach (var id in descendants)
                _directories.Remove(id);
            return new CloudEmpty();
        }
    }

    private CloudEmpty AttachFile(AttachFileRequest request)
    {
        lock (_gate)
        {
            EnsureDirectory(request.DirectoryId);
            if (!_blobs.ContainsKey(request.FileId))
                throw BusinessError(FileNotFoundCode);
            if (_entries.Values.Any(entry => entry.FileId == request.FileId))
                throw BusinessError(FileAlreadyAttachedCode);

            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw BusinessError(DirectoryNameConflictCode);
            name = UniqueName(request.DirectoryId, name);
            var entry = new CloudEntry(Guid.NewGuid().ToString(), request.DirectoryId, request.FileId, name, DateTime.UtcNow);
            _entries.Add(entry.Id, entry);
            return new CloudEmpty();
        }
    }

    private CloudEmpty RenameFileEntry(RenameFileEntryRequest request)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(request.EntryId, out var entry))
                throw BusinessError(FileEntryNotFoundCode);
            var name = request.NewName.Trim();
            if (string.IsNullOrWhiteSpace(name)
                || FileNameExists(entry.DirectoryId, name, ignoreEntryId: entry.Id))
                throw BusinessError(DirectoryNameConflictCode);

            entry.Name = name;
            return new CloudEmpty();
        }
    }

    private CloudEmpty MoveFileEntry(MoveFileEntryRequest request)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(request.EntryId, out var entry))
                throw BusinessError(FileEntryNotFoundCode);
            EnsureDirectory(request.NewDirectoryId);
            if (entry.DirectoryId != request.NewDirectoryId
                && FileNameExists(request.NewDirectoryId, entry.Name, ignoreEntryId: entry.Id))
                throw BusinessError(DirectoryNameConflictCode);

            entry.DirectoryId = request.NewDirectoryId;
            return new CloudEmpty();
        }
    }

    private CloudEmpty DeleteFileEntry(DeleteFileEntryRequest request)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(request.EntryId, out var entry))
                throw BusinessError(FileEntryNotFoundCode);
            MoveToTrash(entry);
            return new CloudEmpty();
        }
    }

    private DeleteFileEntriesResponse DeleteFileEntries(DeleteFileEntriesRequest request)
    {
        lock (_gate)
        {
            var deleted = 0;
            foreach (var entryId in request.EntryIds)
            {
                if (!_entries.TryGetValue(entryId, out var entry))
                    continue;
                MoveToTrash(entry);
                deleted++;
            }

            return new DeleteFileEntriesResponse { DeletedCount = deleted };
        }
    }

    private CloudEmpty RestoreFromTrash(RestoreFromTrashRequest request)
    {
        lock (_gate)
        {
            if (!_trash.TryGetValue(request.EntryId, out var entry))
                throw BusinessError(FileEntryNotFoundCode);
            if (_entries.Values.Any(item => item.FileId == entry.FileId))
                throw BusinessError(FileAlreadyAttachedCode);

            var directoryId = _directories.ContainsKey(entry.DirectoryId) ? entry.DirectoryId : "";
            entry.Name = UniqueName(directoryId, entry.Name);
            entry.DirectoryId = directoryId;
            _trash.Remove(entry.Id);
            _entries.Add(entry.Id, entry);
            return new CloudEmpty();
        }
    }

    private async Task<HttpResponseMessage> HandleHttpAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var segments = (request.RequestUri?.AbsolutePath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 4 && string.Equals(segments[0], "file-upload", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "parts", StringComparison.OrdinalIgnoreCase)
            && request.Method == HttpMethod.Put)
        {
            var sessionId = segments[1];
            if (!int.TryParse(segments[3], out var partNumber))
                return UploadPartFailure(HttpStatusCode.BadRequest, ServerErrors.UploadPartInvalid);
            var data = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var range = request.Content?.Headers.ContentRange;
            var token = request.Headers.TryGetValues("X-Upload-Token", out var values)
                ? values.FirstOrDefault()
                : null;
            lock (_gate)
            {
                if (!_uploadSessions.TryGetValue(sessionId, out var session))
                    return UploadPartFailure(HttpStatusCode.NotFound, ServerErrors.UploadSessionNotFound);
                if (session.Status != UploadSessionStatus.Uploading)
                    return UploadPartFailure(HttpStatusCode.Conflict, ServerErrors.UploadSessionState);
                if (!string.Equals(token, session.UploadToken, StringComparison.Ordinal))
                    return UploadPartFailure(HttpStatusCode.Unauthorized, ServerErrors.UploadTokenInvalid);

                var expectedStart = (long)(partNumber - 1) * session.PartSize;
                var expectedEnd = Math.Min(expectedStart + session.PartSize, session.Size) - 1;
                if (partNumber <= 0 || range is null || range.From != expectedStart || range.To != expectedEnd
                    || range.Length != session.Size || request.Content?.Headers.ContentLength != data.LongLength)
                    return UploadPartFailure(HttpStatusCode.Conflict, ServerErrors.UploadPartInvalid);

                var hasEtag = true;
                if (!_failedUploadPart && FailUploadPartNumberOnce == partNumber)
                {
                    _failedUploadPart = true;
                    hasEtag = false;
                }
                session.Parts[partNumber] = new FakeUploadPart(data, hasEtag);
                if (!hasEtag)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { partNumber, size = data.LongLength }),
                    Encoding.UTF8, "application/json"),
            };
        }

        if (segments.Length == 2 && string.Equals(segments[0], "download", StringComparison.OrdinalIgnoreCase)
            && request.Method == HttpMethod.Get)
        {
            byte[] data;
            lock (_gate)
            {
                if (!_blobs.TryGetValue(segments[1], out var blob))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                data = blob.Data.ToArray();
            }

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range?.From is long start)
            {
                var end = Math.Min(range.To ?? data.LongLength - 1, data.LongLength - 1);
                if (start >= data.LongLength || end < start)
                    return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);

                var body = data[(int)start..((int)end + 1)];
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
                partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.LongLength);
                return partial;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage UploadPartFailure(HttpStatusCode statusCode, string errorCode)
        => new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code = errorCode }),
                Encoding.UTF8, "application/json"),
        };

    private string UniqueName(string directoryId, string name)
    {
        if (!FileNameExists(directoryId, name))
            return name;

        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : "";
        for (var suffix = 1; suffix < 1000; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (!FileNameExists(directoryId, candidate))
                return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    private bool FileNameExists(string directoryId, string name, string? ignoreEntryId = null)
        => _entries.Values.Any(entry => entry.DirectoryId == directoryId
                                         && entry.Id != ignoreEntryId
                                         && string.Equals(entry.Name, name, StringComparison.Ordinal));

    private bool DirectoryNameExists(string parentId, string name, string? ignoreDirectoryId = null)
        => _directories.Values.Any(directory => directory.Id.Length > 0
                                                && directory.ParentId == parentId
                                                && directory.Id != ignoreDirectoryId
                                                && string.Equals(directory.Name, name, StringComparison.Ordinal));

    private bool IsDescendant(string directoryId, string ancestorId)
    {
        while (directoryId.Length > 0 && _directories.TryGetValue(directoryId, out var directory))
        {
            if (directory.Id == ancestorId)
                return true;
            directoryId = directory.ParentId;
        }

        return false;
    }

    private void EnsureDirectory(string directoryId)
    {
        if (!_directories.ContainsKey(directoryId))
            throw BusinessError(DirectoryNotFoundCode);
    }

    private void MoveToTrash(CloudEntry entry)
    {
        _entries.Remove(entry.Id);
        _trash[entry.Id] = entry;
    }

    private static bool IsAfterCursor(CloudEntry entry, string cursorName, string cursorEntryId)
    {
        var nameOrder = string.Compare(entry.Name, cursorName, StringComparison.Ordinal);
        return nameOrder > 0 || (nameOrder == 0 && string.Compare(entry.Id, cursorEntryId, StringComparison.Ordinal) > 0);
    }

    private static ProtoDirectoryInfo ToProto(CloudDirectory directory)
        => new()
        {
            Id = directory.Id,
            ParentId = directory.ParentId,
            Name = directory.Name,
            CreatedAt = ToTimestamp(directory.CreatedAt),
            UpdatedAt = ToTimestamp(directory.UpdatedAt),
        };

    private static FileEntryInfo ToProto(CloudEntry entry)
        => new()
        {
            Id = entry.Id,
            DirectoryId = entry.DirectoryId,
            FileId = entry.FileId,
            Name = entry.Name,
            CreatedAt = ToTimestamp(entry.CreatedAt),
        };

    private static UploadFileInfo ToProto(CloudBlob blob)
        => new()
        {
            Id = blob.Id,
            FileSize = blob.Data.LongLength,
            FileName = blob.Id,
            Type = UploadFileType.CloudFile,
            CreatedAt = ToTimestamp(blob.UploadedAt),
            UploadedAt = ToTimestamp(blob.UploadedAt),
        };

    private static Timestamp ToTimestamp(DateTime dateTime)
        => Timestamp.FromDateTime(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));

    private static FakeCloudFile ToReference(CloudEntry entry)
        => new(entry.Id, entry.FileId, entry.Name, entry.DirectoryId);

    private static Grpc.Core.RpcException BusinessError(string code)
        => FakeCallInvoker.BusinessError(code);

    private sealed class CloudDirectory(string id, string parentId, string name, DateTime createdAt)
    {
        public string Id { get; } = id;
        public string ParentId { get; set; } = parentId;
        public string Name { get; set; } = name;
        public DateTime CreatedAt { get; } = createdAt;
        public DateTime UpdatedAt { get; set; } = createdAt;
    }

    private sealed class CloudEntry(string id, string directoryId, string fileId, string name, DateTime createdAt)
    {
        public string Id { get; } = id;
        public string DirectoryId { get; set; } = directoryId;
        public string FileId { get; } = fileId;
        public string Name { get; set; } = name;
        public DateTime CreatedAt { get; } = createdAt;
    }

    private sealed class CloudBlob(string id, byte[] data)
    {
        public string Id { get; } = id;
        public byte[] Data { get; set; } = data;
        public DateTime UploadedAt { get; } = DateTime.UtcNow;
    }

    private sealed class FakeUploadSession
    {
        public string SessionId { get; init; } = "";
        public string FileId { get; init; } = "";
        public string IdempotencyKey { get; init; } = "";
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public string ContentType { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public long PartSize { get; init; }
        public string UploadToken { get; set; } = "";
        public UploadSessionStatus Status { get; set; }
        public string ErrorMessage { get; set; } = "";
        public Dictionary<int, FakeUploadPart> Parts { get; } = new();
        public byte[]? CompletedData { get; set; }
    }

    private sealed record FakeUploadPart(byte[] Data, bool HasEtag);
}

internal sealed record FakeCloudFile(string EntryId, string FileId, string Name, string DirectoryId);
