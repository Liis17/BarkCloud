using System.Security.AccessControl;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Proto.Files;

using DokanNet;

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

using FileAccess = DokanNet.FileAccess; // в IDokanOperations access — это DokanNet.FileAccess

namespace BarkCloud.Drive.Engine;

internal enum LocalFileKind
{
    Writable,
    Placeholder,
    Pending,
}

internal enum PersistStage
{
    Upload,
    DeleteOld,
    Attach,
    Done,
    Processing,
}

internal sealed class Upload2State
{
    public string IdempotencyKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public string Name { get; set; } = "";
    public string? SessionId { get; set; }
    public int SessionRestarts { get; set; }
    public DateTimeOffset? SessionRestartLimitReachedAtUtc { get; set; }

    public Upload2State Copy() => new()
    {
        IdempotencyKey = IdempotencyKey,
        Sha256 = Sha256,
        Size = Size,
        ContentType = ContentType,
        Name = Name,
        SessionId = SessionId,
        SessionRestarts = SessionRestarts,
        SessionRestartLimitReachedAtUtc = SessionRestartLimitReachedAtUtc,
    };
}

internal sealed class PersistOperation
{
    public PersistStage Stage { get; set; } = PersistStage.Upload;
    public string? FileId { get; set; }
    public Upload2State? Upload2 { get; set; }
    public bool OldEntryDeleted { get; set; }
    public bool AttachAttempted { get; set; }
    public bool AttachCompleted { get; set; }
    public string? SupersededFileId { get; set; }
}

// Общая локальная копия файла для всех открытых хэндлов одного пути.
internal sealed class LocalFile
{
    public required string Path;
    public required string Name;
    public required string DirectoryId;
    public required string TempPath;
    public string? ExistingEntryId;
    public ResolvedNode? ExistingNode;
    public FileStream? Stream;
    public readonly object Sync = new();
    public readonly object DataSync = new();
    public LocalFileKind Kind;
    public int OpenCount;
    public int ActiveCount;
    public bool Modified;
    public bool DirtyAfterPersist;
    public bool DeleteOnClose;
    public bool KeepData;
    public bool Hydrated;
    public PersistOperation? Op;
    public string? OutboxId;
    public DateTime CreatedAt = DateTime.UtcNow;
}

internal sealed class HandleContext
{
    public LocalFile? File;
    public ResolvedNode? Node;
    public bool CleanedUp;
}

// Read-write проекция облака BarkCloud на файловую систему Dokany.
internal sealed class BarkCloudFileSystem : IDokanOperations
{
    internal const NtStatus UnexpectedNetworkError = (NtStatus)0xC00000C4L;

    private const FileAccess WriteBits =
        FileAccess.WriteData | FileAccess.AppendData | FileAccess.GenericWrite | FileAccess.GenericAll;

    private readonly CloudGateway _gateway;
    private readonly TimeProvider _time;
    private readonly string _writeDir;
    private readonly UploadOutbox? _outbox;
    private readonly string _serverKey;
    private readonly Func<string?> _ownerUserId;
    private readonly object _localFilesLock = new();
    private readonly Dictionary<string, LocalFile> _localFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalFile> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalFile> _openOutboxFiles = new(StringComparer.Ordinal);
    internal Action? ReaderLocalFileFoundForTesting { get; set; }
    internal Task? UploadCancellationTaskForTesting { get; private set; }
    private string? _preservedPlaceholderError;

    private sealed class FileSyncLease : IDisposable
    {
        private object? _sync;

        public FileSyncLease(object sync) => _sync = sync;

        public void Dispose()
        {
            if (_sync is not { } sync)
                return;
            _sync = null;
            Monitor.Exit(sync);
        }
    }

    // Метка тома (имя диска), отдаётся в GetVolumeInformation. Устанавливается перед Mount.
    public string VolumeLabel { get; set; } = "BarkCloud";

    // Последняя ошибка загрузки/привязки файла в облако (из Cleanup). Не глушим — отдаём в статус.
    public string? LastSyncError { get; private set; }

    public BarkCloudFileSystem(CloudGateway gateway, string? writeDir = null, UploadOutbox? outbox = null,
        string serverKey = "", Func<string?>? ownerUserId = null, TimeProvider? time = null)
    {
        _gateway = gateway;
        _time = time ?? TimeProvider.System;
        _writeDir = writeDir ?? Path.Combine(Path.GetTempPath(), "BarkCloudDrive", "write");
        _outbox = outbox;
        _serverKey = serverKey;
        _ownerUserId = ownerUserId ?? (() => null);
        Directory.CreateDirectory(_writeDir);

        UpdateOutboxSession(_ownerUserId());
    }

    internal void UpdateOutboxSession(string? userId)
    {
        var entries = string.IsNullOrEmpty(userId) || _outbox is null
            ? Array.Empty<UploadOutboxEntry>()
            : _outbox.Entries.Where(entry => entry.ServerKey == _serverKey && entry.OwnerUserId == userId).ToArray();
        var visibleIds = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

        lock (_localFilesLock)
        {
            foreach (var file in _pendingFiles.Values.ToArray())
            {
                if (file.OutboxId is { } id && visibleIds.Contains(id))
                    continue;

                if (file.OpenCount > 0 && file.OutboxId is { } openId)
                {
                    RemoveLocalFileLocked(file);
                    _openOutboxFiles[openId] = file;
                }
                else
                    RemoveLocalFileLocked(file);
            }

            foreach (var (id, file) in _openOutboxFiles.ToArray())
                if (file.OpenCount == 0 && !visibleIds.Contains(id))
                    _openOutboxFiles.Remove(id);
        }

        foreach (var entry in entries)
        {
            var currentEntry = _outbox!.Find(entry.Id);
            if (currentEntry is null || currentEntry.ServerKey != _serverKey
                || currentEntry.OwnerUserId != userId)
                continue;

            LocalFile? file;
            lock (_localFilesLock)
                file = _pendingFiles.Values.Concat(_openOutboxFiles.Values)
                    .FirstOrDefault(candidate => candidate.OutboxId == entry.Id);
            RegisterOutboxPending(currentEntry, file);
        }
    }

    internal void AdoptOutboxForSession(string userId)
    {
        _outbox?.AdoptUnowned(_serverKey, userId);
        UpdateOutboxSession(userId);
    }

    internal int PendingUploadCount => _outbox?.Count ?? 0;

    internal Action? OutboxAdded { get; set; }

    internal Action<string>? SyncError
    {
        set => _gateway.SyncError = value;
    }

    internal void SetLastSyncError(string message) => LastSyncError = message;

    internal void SetOutboxSyncError(UploadOutboxEntry entry, Exception exception)
        => SetLastSyncError(Loc.T("Eng_SyncFailedFmt", entry.Name, ConnectionErrors.GetDisplayMessage(exception)));

    internal void RegisterOutboxPending(UploadOutboxEntry entry, LocalFile? file = null)
    {
        var operation = CopyOperation(entry.Op ?? new PersistOperation { Stage = entry.Stage, FileId = entry.FileId });
        file ??= new LocalFile
        {
            Path = "",
            Name = entry.Name,
            DirectoryId = entry.DirectoryId,
            TempPath = entry.DataPath,
            ExistingEntryId = entry.ExistingEntryId,
            Kind = LocalFileKind.Pending,
            KeepData = true,
            Hydrated = true,
            Op = operation,
            OutboxId = entry.Id,
            CreatedAt = entry.CreatedAt.UtcDateTime,
        };

        lock (file.Sync)
        {
            file.Kind = LocalFileKind.Pending;
            file.KeepData = true;
            file.Modified = false;
            file.DirtyAfterPersist = false;
            file.TempPath = entry.DataPath;
            file.ExistingEntryId = entry.ExistingEntryId;
            file.Op = operation;
            file.OutboxId = entry.Id;

            lock (_localFilesLock)
            {
                _openOutboxFiles.Remove(entry.Id);
                if (!string.IsNullOrEmpty(file.Path))
                    _localFiles[file.Path] = file;
                _pendingFiles[PendingKey(file.DirectoryId, file.Name)] = file;
            }
        }
    }

    internal bool DeferOutboxDataDeletion(UploadOutboxEntry entry)
    {
        var deferDataDeletion = false;
        lock (_localFilesLock)
        {
            var files = _localFiles.Values.Concat(_pendingFiles.Values).Concat(_openOutboxFiles.Values)
                .Where(file => file.OutboxId == entry.Id).Distinct().ToArray();
            foreach (var file in files)
            {
                if (file.OpenCount > 0)
                {
                    file.DeleteOnClose = true;
                    deferDataDeletion = true;
                }
                else
                    RemoveLocalFileLocked(file);
            }
        }
        return deferDataDeletion;
    }

    internal void CompleteOutboxEntry(UploadOutboxEntry entry)
    {
        lock (_localFilesLock)
        {
            var files = _localFiles.Values.Concat(_pendingFiles.Values).Concat(_openOutboxFiles.Values)
                .Where(file => file.OutboxId == entry.Id).Distinct().ToArray();
            foreach (var file in files)
                if (file.OpenCount == 0 || !file.DeleteOnClose)
                    RemoveLocalFileLocked(file);
        }
        ClearLastSyncErrorIfNoPendingUploads();
    }

    internal bool RegisterLocalFile(LocalFile file)
    {
        lock (_localFilesLock)
        {
            file.Path = NormalizePath(file.Path);
            return _localFiles.TryAdd(file.Path, file);
        }
    }

    internal void RemovePlaceholdersForAccountChange()
    {
        LocalFile[] placeholders;
        lock (_localFilesLock)
            placeholders = _localFiles.Values.Where(file => file.Kind == LocalFileKind.Placeholder).ToArray();

        foreach (var file in placeholders)
        {
            lock (file.Sync)
            lock (file.DataSync)
            {
                bool hasData;
                lock (_localFilesLock)
                {
                    if (!_localFiles.TryGetValue(file.Path, out var current) || !ReferenceEquals(current, file)
                        || file.Kind != LocalFileKind.Placeholder)
                        continue;

                    hasData = file.Modified || file.KeepData
                              || (file.Stream?.Length
                                  ?? (File.Exists(file.TempPath) ? new FileInfo(file.TempPath).Length : 0)) > 0;
                    RemoveLocalFileLocked(file);
                }

                file.Stream?.Dispose();
                file.Stream = null;
                if (hasData)
                {
                    var warning = Loc.T("Eng_PlaceholderDataPreservedFmt", file.Name, file.TempPath);
                    _preservedPlaceholderError = warning;
                    LastSyncError = warning;
                    EngineLog.Error(warning, new IOException(warning));
                }
                else
                    TryDelete(file.TempPath);
            }
        }
    }

    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        try
        {
            return CreateFileCore(fileName, access, share, mode, options, attributes, info);
        }
        catch (Exception ex)
        {
            EngineLog.Error($"CreateFile(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    private NtStatus CreateFileCore(string fileName, FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        var wantsWrite = mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate
                         || (access & WriteBits) != 0;
        ResolvedNode? node;

        if (info.IsDirectory)
        {
            node = SafeResolve(fileName);
        }
        else
        {
            if (TryOpenRegisteredLocal(fileName, mode, wantsWrite, info, out var localContext, out var localStatus))
            {
                if (localStatus != DokanResult.Success)
                    return localStatus;
                info.Context = localContext;
                return ExistingStatus(mode);
            }

            if (TryOpenPendingByPath(fileName, mode, wantsWrite, out localContext, out localStatus))
            {
                if (localStatus != DokanResult.Success)
                    return localStatus;
                info.Context = localContext;
                return ExistingStatus(mode);
            }

            node = SafeResolve(fileName);

            // Разрешение пути могло завершиться одновременно с регистрацией рабочей копии.
            if (TryOpenRegisteredLocal(fileName, mode, wantsWrite, info, out localContext, out localStatus))
            {
                if (localStatus != DokanResult.Success)
                    return localStatus;
                info.Context = localContext;
                return ExistingStatus(mode);
            }

            if (TryOpenPendingByPath(fileName, mode, wantsWrite, out localContext, out localStatus))
            {
                if (localStatus != DokanResult.Success)
                    return localStatus;
                info.Context = localContext;
                return ExistingStatus(mode);
            }
        }

        // ───────── папки ─────────
        if (info.IsDirectory)
        {
            if (node != null)
                return node.IsDirectory ? DokanResult.Success : DokanResult.NotADirectory;

            if (mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate)
            {
                var parent = _gateway.ResolveParentDirectory(fileName);
                if (parent is null)
                    return DokanResult.PathNotFound;
                try
                {
                    _gateway.CreateDirectory(parent.Value.DirectoryId, parent.Value.Name);
                    _gateway.InvalidateListing(parent.Value.DirectoryId);
                    info.IsDirectory = true;
                    return DokanResult.Success;
                }
                catch (Exception ex)
                {
                    EngineLog.Error($"CreateDirectory(\"{fileName}\")", ex);
                    return ToStatus(ex);
                }
            }

            return DokanResult.PathNotFound;
        }

        // путь оказался папкой — открываем как папку
        if (node is { IsDirectory: true })
        {
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        // ───────── существующий файл ─────────
        if (node != null)
        {
            if (mode == FileMode.CreateNew)
                return DokanResult.FileExists;

            if (!wantsWrite)
            {
                info.Context = new HandleContext { Node = node };
                return ExistingStatus(mode);
            }

            try
            {
                var candidate = OpenExistingForWrite(fileName, node,
                    truncate: mode is FileMode.Create or FileMode.Truncate);
                if (!TryOpenCandidate(candidate, mode, info, out var context, out var alreadyExists, out var candidateStatus))
                    return candidateStatus;
                info.Context = context;
                return ExistingStatus(mode);
            }
            catch (Exception ex)
            {
                EngineLog.Error($"OpenForWrite(\"{fileName}\")", ex);
                return ToStatus(ex);
            }
        }

        // ───────── новый файл ─────────
        if (mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate)
        {
            var parent = _gateway.ResolveParentDirectory(fileName);
            if (parent is null)
                return DokanResult.PathNotFound;
            var candidate = OpenNew(fileName, parent.Value.DirectoryId, parent.Value.Name);
            if (!TryOpenCandidate(candidate, mode, info, out var context, out var alreadyExists, out var candidateStatus))
                return candidateStatus;
            info.Context = context;
            return alreadyExists ? ExistingStatus(mode) : DokanResult.Success;
        }

        return DokanResult.FileNotFound;
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        try
        {
            if (info.Context is HandleContext { File: { } file } handle)
            {
                var becameInactive = false;
                var deleteNow = false;
                var persist = false;

                lock (_localFilesLock)
                {
                    var wasDeletePending = file.DeleteOnClose;
                    if (info.DeletePending && file.Kind != LocalFileKind.Pending)
                        file.DeleteOnClose = true;

                    if (!handle.CleanedUp)
                    {
                        handle.CleanedUp = true;
                        if (file.ActiveCount > 0)
                            file.ActiveCount--;
                        becameInactive = file.ActiveCount == 0;
                    }

                    deleteNow = file.Kind != LocalFileKind.Pending && file.DeleteOnClose && file.ActiveCount == 0
                                && (becameInactive || (!wasDeletePending && info.DeletePending));
                    persist = !file.DeleteOnClose && file.Kind != LocalFileKind.Pending && becameInactive;
                }

                if (deleteNow)
                    DeleteLocalFile(file);
                else if (persist)
                {
                    using var keepAlive = DokanKeepAlive.Start(info, _time);
                    lock (file.Sync)
                    {
                        if (file.Modified && file.Op?.Stage != PersistStage.Processing)
                            Persist(file);
                    }
                }
            }
            else if (info.Context is HandleContext nodeHandleContext)
            {
                if (!nodeHandleContext.CleanedUp)
                {
                    nodeHandleContext.CleanedUp = true;
                    if (info.DeletePending)
                    {
                        if (nodeHandleContext.Node is { IsDirectory: false } node)
                            _gateway.QueueDeleteFileEntry(node.EntryId, node.DirectoryId);
                        else
                            DeleteNode(fileName);
                    }
                }
            }
            else if (info.DeletePending && (info.IsDirectory
                     || FindLocalFile(fileName)?.Kind != LocalFileKind.Pending
                     && FindPendingFileByPath(fileName) is null))
            {
                DeleteNode(fileName);
            }
        }
        catch (UploadProcessingPendingException ex)
        {
            EngineLog.Info(ex.Message);
        }
        catch (Exception ex)
        {
            // диск не должен падать, но причину фиксируем — и не глушим: при сбое синхронизации
            // записи в облако выставляем ошибку в статус, чтобы App показал её пользователю.
            if (info.Context is HandleContext { File: { } file })
            {
                using (AcquireFileSync(file, info))
                    file.KeepData = true;
            }
            EngineLog.Error($"Cleanup(\"{fileName}\")", ex);
            if (info.Context is HandleContext { File: not null })
            {
                LastSyncError = Loc.T("Eng_SyncFailedFmt", Path.GetFileName(fileName), ConnectionErrors.GetDisplayMessage(ex));
            }
            else if (info.DeletePending)
            {
                LastSyncError = info.IsDirectory
                    || info.Context is HandleContext { Node: { IsDirectory: true } }
                    ? Loc.T("Eng_DirectoryDeleteFailedFmt", Path.GetFileName(fileName), ConnectionErrors.GetDisplayMessage(ex))
                    : Loc.T("Eng_SyncFailedFmt", Path.GetFileName(fileName), ConnectionErrors.GetDisplayMessage(ex));
            }
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        try
        {
            if (info.Context is HandleContext { File: { } file })
                CloseLocalFile(fileName, file, info);
        }
        catch (Exception ex)
        {
            EngineLog.Error($"CloseFile(\"{fileName}\")", ex);
            if (info.Context is HandleContext { File: { } file })
            {
                using (AcquireFileSync(file, info))
                    file.KeepData = true;
                LastSyncError = Loc.T("Eng_SyncFailedFmt", Path.GetFileName(fileName), ConnectionErrors.GetDisplayMessage(ex));
            }
        }
        finally
        {
            info.Context = null;
        }
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        try
        {
            var handle = info.Context as HandleContext;
            var readerNode = handle?.File is null && handle?.Node is { IsDirectory: false } nodeHandle
                ? nodeHandle
                : null;
            var file = handle?.File;
            if (file is null && readerNode is not null)
            {
                file = FindLocalFileByEntryId(readerNode.EntryId);
                if (file is not null)
                    ReaderLocalFileFoundForTesting?.Invoke();
            }
            if (file is null && handle?.Node is null)
                file = FindLocalFile(fileName) ?? FindPendingFileByPath(fileName);

            if (file is not null && readerNode is not null)
            {
                bool hydrated;
                lock (file.DataSync)
                {
                    if (!IsRegisteredReadableLocalFile(file, readerNode.EntryId))
                        file = null;
                    hydrated = file?.Hydrated == true;
                    if (file is not null && hydrated)
                        bytesRead = ReadLocalCopy(file, buffer, offset);
                }

                if (file is not null && !hydrated)
                {
                    using (AcquireFileSync(file, info))
                    {
                        lock (file.DataSync)
                        {
                            if (!IsRegisteredReadableLocalFile(file, readerNode.EntryId))
                                file = null;
                            hydrated = file?.Hydrated == true;
                        }

                        if (file is not null)
                        {
                            if (!hydrated)
                                EnsureHydrated(file, info);
                            lock (file.DataSync)
                            {
                                if (!IsRegisteredReadableLocalFile(file, readerNode.EntryId))
                                    file = null;
                                else
                                    bytesRead = ReadLocalCopy(file, buffer, offset);
                            }
                        }
                    }
                }
            }
            else if (file is not null)
            {
                bool hydrated;
                lock (file.DataSync)
                    hydrated = file.Hydrated;
                if (!hydrated)
                {
                    using (AcquireFileSync(file, info))
                    {
                        lock (file.DataSync)
                            hydrated = file.Hydrated;
                        if (!hydrated)
                            EnsureHydrated(file, info);
                    }
                }

                bytesRead = ReadLocalCopy(file, buffer, offset);
            }

            if (file is not null)
                return DokanResult.Success;

            var node = handle?.Node ?? SafeResolve(fileName);
            if (node is null || node.IsDirectory)
                return DokanResult.FileNotFound;

            bytesRead = _gateway.Read(node.FileId, node.Length, buffer, offset,
                () => DokanKeepAlive.Start(info, _time));
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"ReadFile(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        try
        {
            if (info.Context is not HandleContext { File: { } file })
                return DokanResult.AccessDenied;

            using (AcquireFileSync(file, info))
            {
                if (file.Kind == LocalFileKind.Pending)
                    return DokanResult.AccessDenied;
                var appendToEnd = info.WriteToEndOfFile;
                bool hydrate;
                lock (file.DataSync)
                {
                    var length = LocalFileLengthLocked(file);
                    hydrate = !file.Hydrated
                              && (appendToEnd || offset != 0 || buffer.LongLength < length);
                }

                if (hydrate)
                    EnsureHydrated(file, info);

                lock (file.DataSync)
                {
                    if (!file.Hydrated)
                        CreateEmptyStream(file);
                    var stream = EnsureStreamOpen(file, writable: true);
                    stream.Position = appendToEnd ? stream.Length : offset;
                    stream.Write(buffer, 0, buffer.Length);
                    MarkModified(file);
                }
            }
            bytesWritten = buffer.Length;
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"WriteFile(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        try
        {
            if (info.Context is HandleContext { File: { } file })
                lock (file.DataSync) file.Stream?.Flush();
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"FlushFileBuffers(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        fileInfo = default;
        try
        {
            var handle = info.Context as HandleContext;
            var localFile = handle?.File;
            if (localFile is null && handle?.Node is { IsDirectory: false } nodeHandle)
                localFile = FindLocalFileByEntryId(nodeHandle.EntryId);
            if (localFile is null && handle?.Node is null)
                localFile = FindLocalFile(fileName) ?? FindPendingFileByPath(fileName);
            if (localFile != null)
            {
                fileInfo = GetLocalFileInformation(localFile);
                return DokanResult.Success;
            }

            var node = handle?.Node ?? SafeResolve(fileName);
            if (node is null)
                return DokanResult.FileNotFound;

            fileInfo = new FileInformation
            {
                FileName = node.Name,
                Attributes = node.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                Length = node.Length,
                CreationTime = node.Created,
                LastWriteTime = node.Updated,
                LastAccessTime = node.Updated,
            };
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"GetFileInformation(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        try
        {
            var registeredFiles = GetLocalFiles();
            var node = SafeResolve(fileName);
            if (node is null || !node.IsDirectory)
                return DokanResult.FileNotFound;

            var listing = _gateway.ListDirectory(node.DirectoryId);
            var localFiles = registeredFiles.Where(file => file.DirectoryId == node.DirectoryId).ToList();
            var included = new HashSet<LocalFile>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in listing.Subdirs)
            {
                files.Add(new FileInformation
                {
                    FileName = d.Name,
                    Attributes = FileAttributes.Directory,
                    CreationTime = ToDate(d.CreatedAt),
                    LastWriteTime = ToDate(d.UpdatedAt),
                    LastAccessTime = ToDate(d.UpdatedAt),
                    Length = 0,
                });
                names.Add(d.Name);
            }

            foreach (var f in listing.Files)
            {
                var localFile = localFiles.FirstOrDefault(local =>
                    (local.ExistingEntryId != null && local.ExistingEntryId == f.Entry.Id)
                    || string.Equals(local.Name, f.Entry.Name, StringComparison.OrdinalIgnoreCase));
                if (localFile != null)
                {
                    files.Add(GetLocalFileInformation(localFile));
                    included.Add(localFile);
                    names.Add(localFile.Name);
                    continue;
                }

                files.Add(new FileInformation
                {
                    FileName = f.Entry.Name,
                    Attributes = FileAttributes.Normal,
                    CreationTime = ToDate(f.Entry.CreatedAt),
                    LastWriteTime = ToDate(f.File.UploadedAt),
                    LastAccessTime = ToDate(f.File.UploadedAt),
                    Length = f.File.FileSize,
                });
                names.Add(f.Entry.Name);
            }

            foreach (var localFile in localFiles)
            {
                if (included.Contains(localFile) || !names.Add(localFile.Name))
                    continue;
                files.Add(GetLocalFileInformation(localFile));
            }

            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"FindFiles(\"{fileName}\")", ex);
            files = new List<FileInformation>();
            return ToStatus(ex);
        }
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern,
        out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        return DokanResult.NotImplemented; // → Dokan отфильтрует результат FindFiles
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        try
        {
            if (info.Context is not HandleContext { File: { } file })
                return DokanResult.AccessDenied;
            using (AcquireFileSync(file, info))
            {
                if (file.Kind == LocalFileKind.Pending)
                    return DokanResult.AccessDenied;
                if (length == 0)
                    TruncateLocalFileToZero(file);
                else
                {
                    lock (file.DataSync)
                        if (length == LocalFileLengthLocked(file))
                            return DokanResult.Success;

                    EnsureHydrated(file, info);
                    lock (file.DataSync)
                    {
                        EnsureStreamOpen(file, writable: true).SetLength(length);
                        MarkModified(file);
                    }
                }
            }
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"SetEndOfFile(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        try
        {
            if (info.Context is not HandleContext { File: { } file })
                return DokanResult.AccessDenied;
            using (AcquireFileSync(file, info))
            {
                if (file.Kind == LocalFileKind.Pending)
                    return DokanResult.AccessDenied;
                if (length == 0)
                    TruncateLocalFileToZero(file);
                else
                {
                    lock (file.DataSync)
                        if (length >= LocalFileLengthLocked(file))
                            return DokanResult.Success;

                    EnsureHydrated(file, info);
                    lock (file.DataSync)
                    {
                        var stream = EnsureStreamOpen(file, writable: true);
                        if (length < stream.Length)
                        {
                            stream.SetLength(length);
                            MarkModified(file);
                        }
                    }
                }
            }
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"SetAllocationSize(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        try
        {
            return MoveFileCore(oldName, newName, replace, info);
        }
        catch (Exception ex)
        {
            EngineLog.Error($"MoveFile(\"{oldName}\" → \"{newName}\")", ex);
            return ToStatus(ex);
        }
    }

    private NtStatus MoveFileCore(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        // Проверка до Sync сохраняет AccessDenied, даже если worker удерживает Sync у Pending;
        // MoveFileLocked повторяет её после захвата Sync, чтобы закрыть гонку.
        if (FindLocalFile(oldName)?.Kind == LocalFileKind.Pending
            || FindLocalFile(newName)?.Kind == LocalFileKind.Pending)
            return DokanResult.AccessDenied;

        var sourceCandidate = FindLocalFile(oldName);
        if (sourceCandidate is null)
            return MoveFileLocked(oldName, newName, replace, info.IsDirectory, null);

        if (!Monitor.TryEnter(sourceCandidate.Sync, 0))
            return DokanResult.SharingViolation;
        try
        {
            return MoveFileLocked(oldName, newName, replace, info.IsDirectory, sourceCandidate);
        }
        finally
        {
            Monitor.Exit(sourceCandidate.Sync);
        }
    }

    private NtStatus MoveFileLocked(string oldName, string newName, bool replace, bool sourceIsDirectory,
        LocalFile? sourceCandidate)
    {
        if (FindLocalFile(oldName)?.Kind == LocalFileKind.Pending
            || FindLocalFile(newName)?.Kind == LocalFileKind.Pending
            || FindPendingFileByPath(oldName) is not null
            || FindPendingFileByPath(newName) is not null)
            return DokanResult.AccessDenied;

        LocalFile? sourceFile = sourceCandidate;
        if (sourceFile is not null)
        {
            lock (_localFilesLock)
                if (!_localFiles.TryGetValue(NormalizePath(oldName), out var registered)
                    || !ReferenceEquals(registered, sourceFile))
                    sourceFile = null;
        }

        var from = _gateway.ResolveParentDirectory(oldName);
        var to = _gateway.ResolveParentDirectory(newName);
        if (from is null || to is null)
            return DokanResult.PathNotFound;

        ResolvedNode? ResolveSource()
        {
            if (sourceFile is null)
                return ResolveMoveSource(from.Value.DirectoryId, from.Value.Name, sourceIsDirectory, null);
            return sourceFile.ExistingEntryId is null
                ? null
                : ResolveMoveSource(from.Value.DirectoryId, from.Value.Name, isDirectory: false,
                    sourceFile.ExistingEntryId);
        }

        LocalFile? targetFile = null;
        var targetSyncHeld = false;
        try
        {
            var sourceNode = ResolveSource();
            if (sourceFile is null && sourceNode is null
                || sourceFile?.ExistingEntryId is not null && sourceNode is null)
                return DokanResult.FileNotFound;

            if (_gateway.HasTombstonedFile(to.Value.DirectoryId, to.Value.Name))
            {
                _gateway.FlushPending(throwOnFailure: true);
                sourceNode = ResolveSource();
                if (sourceFile is null && sourceNode is null
                    || sourceFile?.ExistingEntryId is not null && sourceNode is null)
                    return DokanResult.FileNotFound;
            }

            targetFile = FindLocalFile(newName);

            var isDirectory = sourceFile is null && sourceNode!.IsDirectory;
            var sourceEntryId = sourceFile is null
                ? sourceNode is { IsDirectory: false } ? sourceNode.EntryId : null
                : sourceFile.ExistingEntryId;
            var targetNode = ResolveMoveTarget(to.Value.DirectoryId, to.Value.Name);
            var targetEntryId = targetNode is { IsDirectory: false }
                ? targetNode.EntryId
                : targetNode is null ? targetFile?.ExistingEntryId : null;
            var targetExists = targetNode is not null || targetFile is not null;
            var sameObject = isDirectory
                ? targetNode is { IsDirectory: true }
                  && string.Equals(sourceNode!.DirectoryId, targetNode.DirectoryId, StringComparison.Ordinal)
                : targetNode?.IsDirectory != true
                  && ((sourceEntryId is not null && sourceEntryId == targetEntryId)
                      || (sourceFile is not null && ReferenceEquals(sourceFile, targetFile)));

            if (targetExists && !sameObject && isDirectory != (targetNode?.IsDirectory == true))
                return DokanResult.AccessDenied;

            // Документация MoveFileEx запрещает заменять уже существующую папку.
            if (targetExists && !sameObject && isDirectory)
                return DokanResult.FileExists;

            if (targetFile is not null && !sameObject && !ReferenceEquals(sourceFile, targetFile)
                && IsLocalFileOpen(targetFile))
                return DokanResult.SharingViolation;

            if (targetFile is not null && !ReferenceEquals(targetFile, sourceFile))
            {
                targetSyncHeld = Monitor.TryEnter(targetFile.Sync, 0);
                if (!targetSyncHeld)
                    return DokanResult.SharingViolation;
            }

            if (targetExists && !sameObject && !replace)
                return DokanResult.FileExists;

            var sourceName = sourceNode?.Name ?? sourceFile!.Name;
            var sourceDirectoryId = isDirectory
                ? from.Value.DirectoryId
                : sourceNode?.DirectoryId ?? sourceFile!.DirectoryId;
            var parentChanged = !string.Equals(sourceDirectoryId, to.Value.DirectoryId, StringComparison.Ordinal);
            var nameChanged = !string.Equals(sourceName, to.Value.Name, StringComparison.Ordinal);

            if (sourceFile is not null && sourceFile.ExistingEntryId is null)
            {
                var existingEntryId = replace && !sameObject ? targetEntryId : null;
                var existingNode = existingEntryId is null
                    ? sourceFile.ExistingNode
                    : targetNode ?? targetFile?.ExistingNode;
                try
                {
                    var discarded = CommitLocalFileMove(sourceFile, newName, to.Value.DirectoryId,
                        targetFile, existingEntryId, existingNode);
                    if (discarded is not null)
                        DisposeCandidate(discarded);
                }
                catch (MoveStatusException ex)
                {
                    return ex.Status;
                }

                return DokanResult.Success;
            }

            var replaceEntryId = replace && !sameObject && !isDirectory ? targetEntryId : null;
            var rollback = new Stack<Action>();
            var targetDeleted = false;
            try
            {
                if (replaceEntryId is not null)
                {
                    try
                    {
                        _gateway.DeleteFileEntry(replaceEntryId);
                        targetDeleted = true;
                    }
                    catch (Exception ex) when (IsUncertainMoveResult(ex))
                    {
                        targetDeleted = true;
                        throw;
                    }
                }

                MoveCloudNode(sourceNode!, sourceDirectoryId, sourceName, to.Value.DirectoryId, to.Value.Name,
                    parentChanged, nameChanged, rollback);

                if (sourceFile is not null)
                {
                    var movedNode = sourceNode is { IsDirectory: false }
                        ? CopyNode(sourceNode, to.Value.DirectoryId, to.Value.Name)
                        : sourceFile.ExistingNode;
                    var discarded = CommitLocalFileMove(sourceFile, newName, to.Value.DirectoryId,
                        targetFile, sourceEntryId, movedNode);
                    if (discarded is not null)
                        DisposeCandidate(discarded);
                }
                else if (targetFile is not null && !sameObject)
                {
                    var discarded = RemoveClosedTargetFile(targetFile);
                    if (discarded is not null)
                        DisposeCandidate(discarded);
                }

                if (isDirectory)
                    MoveLocalFilesUnderDirectory(oldName, newName);
            }
            catch (Exception ex)
            {
                var rollbackErrors = RollbackMove(rollback, oldName);
                if (targetDeleted)
                {
                    try
                    {
                        _gateway.RestoreFromTrash(replaceEntryId!, to.Value.DirectoryId);
                    }
                    catch (Exception restoreError)
                    {
                        EngineLog.Error($"MoveFile restore target \"{newName}\"", restoreError);
                        if (!IsExpectedMoveRollbackError(restoreError))
                            rollbackErrors.Add(restoreError);
                    }
                }

                if (rollbackErrors.Count > 0)
                    LastSyncError = Loc.T("Eng_MoveRollbackFailedFmt", oldName,
                        string.Join("; ", rollbackErrors.Select(ConnectionErrors.GetDisplayMessage)));

                if (ex is MoveStatusException statusException)
                    return statusException.Status;
                throw;
            }

            return DokanResult.Success;
        }
        finally
        {
            if (targetSyncHeld)
                Monitor.Exit(targetFile!.Sync);
            _gateway.InvalidateListing(from.Value.DirectoryId);
            _gateway.InvalidateListing(to.Value.DirectoryId);
        }
    }

    private bool IsLocalFileOpen(LocalFile file)
    {
        lock (_localFilesLock)
            return file.OpenCount > 0;
    }

    private ResolvedNode? ResolveMoveSource(string directoryId, string name, bool isDirectory, string? entryId)
    {
        var listing = _gateway.ListDirectory(directoryId);
        if (entryId is not null)
        {
            var file = listing.Files.FirstOrDefault(item =>
                string.Equals(item.Entry.Id, entryId, StringComparison.Ordinal));
            return file is null ? null : CopyNode(file);
        }

        if (isDirectory)
        {
            var directory = _gateway.FindSubdirectoryByName(directoryId, listing, name, exactFirst: true);
            return directory is null ? null : new ResolvedNode
            {
                IsDirectory = true,
                DirectoryId = directory.Id,
                Name = directory.Name,
                Created = ToDate(directory.CreatedAt),
                Updated = ToDate(directory.UpdatedAt),
            };
        }

        var source = _gateway.FindFileByName(directoryId, listing, name, exactFirst: true);
        return source is null ? null : CopyNode(source);
    }

    private ResolvedNode? ResolveMoveTarget(string directoryId, string name)
    {
        var listing = _gateway.ListDirectory(directoryId);
        var directory = _gateway.FindSubdirectoryByName(directoryId, listing, name, exactFirst: true);
        if (directory is not null)
            return new ResolvedNode
            {
                IsDirectory = true,
                DirectoryId = directory.Id,
                Name = directory.Name,
                Created = ToDate(directory.CreatedAt),
                Updated = ToDate(directory.UpdatedAt),
            };

        var file = _gateway.FindFileByName(directoryId, listing, name, exactFirst: true);
        return file is null ? null : CopyNode(file);
    }

    private static ResolvedNode CopyNode(FileEntryDetailed file)
        => new()
        {
            DirectoryId = file.Entry.DirectoryId,
            EntryId = file.Entry.Id,
            FileId = file.File.Id,
            Name = file.Entry.Name,
            Length = file.File.FileSize,
            Created = ToDate(file.Entry.CreatedAt),
            Updated = ToDate(file.File.UploadedAt),
        };

    private LocalFile? CommitLocalFileMove(LocalFile file, string newName, string newDirectoryId,
        LocalFile? targetFile, string? existingEntryId, ResolvedNode? existingNode)
    {
        LocalFile? discarded = null;
        var newPath = NormalizePath(newName);
        var targetSyncHeld = false;
        if (targetFile is not null && !ReferenceEquals(targetFile, file))
        {
            targetSyncHeld = Monitor.TryEnter(targetFile.Sync, 0);
            if (!targetSyncHeld)
                throw new MoveStatusException(DokanResult.SharingViolation);
        }

        try
        {
            lock (file.DataSync)
            lock (_localFilesLock)
            {
                if (file.Kind == LocalFileKind.Pending)
                    throw new MoveStatusException(DokanResult.AccessDenied);
                if (!_localFiles.TryGetValue(file.Path, out var source) || !ReferenceEquals(source, file))
                    throw new MoveStatusException(DokanResult.FileNotFound);

                if (_localFiles.TryGetValue(newPath, out var currentTarget) && !ReferenceEquals(currentTarget, file))
                {
                    if (currentTarget.Kind == LocalFileKind.Pending)
                        throw new MoveStatusException(DokanResult.AccessDenied);
                    if (currentTarget.OpenCount > 0)
                        throw new MoveStatusException(DokanResult.SharingViolation);
                    RemoveLocalFileLocked(currentTarget);
                    discarded = currentTarget;
                }
                else if (targetFile is not null && !ReferenceEquals(targetFile, file)
                         && targetFile.OpenCount > 0)
                    throw new MoveStatusException(DokanResult.SharingViolation);

                _localFiles.Remove(file.Path);
                file.Path = newPath;
                file.Name = Path.GetFileName(newPath);
                file.DirectoryId = newDirectoryId;
                file.ExistingEntryId = existingEntryId;
                file.ExistingNode = existingNode;
                _localFiles[file.Path] = file;
            }
        }
        finally
        {
            if (targetSyncHeld)
                Monitor.Exit(targetFile!.Sync);
        }
        return discarded;
    }

    private LocalFile? RemoveClosedTargetFile(LocalFile file)
    {
        if (!Monitor.TryEnter(file.Sync, 0))
            throw new MoveStatusException(DokanResult.SharingViolation);
        try
        {
            lock (_localFilesLock)
            {
                if (!_localFiles.TryGetValue(file.Path, out var current) || !ReferenceEquals(current, file))
                    return null;
                if (file.Kind == LocalFileKind.Pending)
                    throw new MoveStatusException(DokanResult.AccessDenied);
                if (file.OpenCount > 0)
                    throw new MoveStatusException(DokanResult.SharingViolation);
                RemoveLocalFileLocked(file);
                return file;
            }
        }
        finally
        {
            Monitor.Exit(file.Sync);
        }
    }

    private void MoveCloudNode(ResolvedNode node, string sourceDirectoryId, string sourceName,
        string destinationDirectoryId, string destinationName, bool parentChanged, bool nameChanged,
        Stack<Action> rollback)
    {
        void Rename(string name)
        {
            if (node.IsDirectory)
                _gateway.RenameDirectory(node.DirectoryId, name);
            else
                _gateway.RenameFileEntry(node.EntryId, name);
        }

        void Move(string directoryId)
        {
            if (node.IsDirectory)
                _gateway.MoveDirectory(node.DirectoryId, directoryId);
            else
                _gateway.MoveFileEntry(node.EntryId, directoryId);
        }

        void Apply(Action action, Action undo, string? conflictName = null)
        {
            try
            {
                action();
            }
            catch (RpcException ex) when (conflictName is not null
                                          && ServerErrors.HasCode(ex, ServerErrors.DirectoryNameConflict)
                                          && _gateway.HasTombstonedFile(destinationDirectoryId, conflictName))
            {
                _gateway.FlushPending(throwOnFailure: true);
                try
                {
                    action();
                }
                catch (Exception retryError) when (IsUncertainMoveResult(retryError))
                {
                    rollback.Push(undo);
                    throw;
                }
            }
            catch (Exception ex) when (IsUncertainMoveResult(ex))
            {
                rollback.Push(undo);
                throw;
            }

            rollback.Push(undo);
        }

        if (parentChanged && nameChanged)
        {
            var temporaryName = ".~bc-" + Guid.NewGuid().ToString("N")[..8];
            Apply(() => Rename(temporaryName), () => Rename(sourceName));
            Apply(() => Move(destinationDirectoryId), () => Move(sourceDirectoryId));
            Apply(() => Rename(destinationName), () => Rename(temporaryName),
                node.IsDirectory ? null : destinationName);
        }
        else if (parentChanged)
        {
            Apply(() => Move(destinationDirectoryId), () => Move(sourceDirectoryId),
                node.IsDirectory ? null : sourceName);
        }
        else if (nameChanged)
        {
            Apply(() => Rename(destinationName), () => Rename(sourceName),
                node.IsDirectory ? null : destinationName);
        }
    }

    private List<Exception> RollbackMove(Stack<Action> rollback, string oldName)
    {
        var errors = new List<Exception>();
        while (rollback.TryPop(out var undo))
        {
            try
            {
                undo();
            }
            catch (Exception ex)
            {
                EngineLog.Error($"MoveFile rollback \"{oldName}\"", ex);
                if (!IsExpectedMoveRollbackError(ex))
                    errors.Add(ex);
            }
        }
        return errors;
    }

    private void MoveLocalFilesUnderDirectory(string oldName, string newName)
    {
        var oldPrefix = NormalizePath(oldName) + "\\";
        var newPrefix = NormalizePath(newName) + "\\";
        lock (_localFilesLock)
        {
            var updates = _localFiles.Values.Distinct()
                .Where(file => file.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Select(file => (File: file, OldPath: file.Path,
                    NewPath: newPrefix + file.Path[oldPrefix.Length..]))
                .ToArray();
            var moving = updates.Select(update => update.File).ToHashSet();
            foreach (var update in updates)
                if (_localFiles.TryGetValue(update.NewPath, out var current)
                    && !ReferenceEquals(current, update.File) && !moving.Contains(current))
                    throw new MoveStatusException(current.OpenCount > 0
                        ? DokanResult.SharingViolation
                        : DokanResult.FileExists);

            foreach (var update in updates)
                _localFiles.Remove(update.OldPath);
            foreach (var update in updates)
            {
                update.File.Path = update.NewPath;
                _localFiles[update.NewPath] = update.File;
            }
        }
    }

    private static ResolvedNode CopyNode(ResolvedNode node, string directoryId, string name)
        => new()
        {
            IsDirectory = node.IsDirectory,
            DirectoryId = directoryId,
            EntryId = node.EntryId,
            FileId = node.FileId,
            Name = name,
            Length = node.Length,
            Created = node.Created,
            Updated = node.Updated,
        };

    private sealed class MoveStatusException(NtStatus status) : Exception
    {
        public NtStatus Status { get; } = status;
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        try
        {
            var localFile = FindLocalFile(fileName);
            if (localFile?.Kind == LocalFileKind.Pending || FindPendingFileByPath(fileName) is not null)
                return DokanResult.AccessDenied;
            if (localFile is not null)
                return DokanResult.Success;

            var node = SafeResolve(fileName);
            if (node is null)
                return DokanResult.FileNotFound;
            return node.IsDirectory ? DokanResult.AccessDenied : DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"DeleteFile(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        try
        {
            var node = SafeResolve(fileName);
            if (node is null || !node.IsDirectory)
                return DokanResult.Success;

            _gateway.InvalidateListing(node.DirectoryId);
            if (DirectoryIsNotEmpty(fileName, node))
                return DokanResult.DirectoryNotEmpty;

            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"DeleteDirectory(\"{fileName}\")", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        freeBytesAvailable = totalNumberOfBytes = totalNumberOfFreeBytes = 0;
        try
        {
            var s = _gateway.GetStorage();
            var total = s.StorageLimit > 0 ? s.StorageLimit : s.TotalAvailableStorage;
            var used = s.StorageLimit > 0
                ? s.TotalUsedStorage + s.ReservedStorage
                : s.DiskUsedStorage + s.S3UsedStorage;
            var free = Math.Max(0, total - used);
            totalNumberOfBytes = total;
            freeBytesAvailable = free;
            totalNumberOfFreeBytes = free;
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            EngineLog.Error("GetDiskFreeSpace", ex);
            return ToStatus(ex);
        }
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = VolumeLabel;
        fileSystemName = "BarkCloudFS";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
        return DokanResult.Success;
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus Unmounted(IDokanFileInfo info) => DokanResult.Success;

    // Атрибуты/времена не моделируем — отвечаем успехом, чтобы сохранение из редакторов не падало.
    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = new List<FileInformation>();
        return DokanResult.NotImplemented;
    }

    // ───────── внутреннее ─────────

    private bool TryOpenRegisteredLocal(string path, FileMode mode, bool wantsWrite, IDokanFileInfo info,
        out HandleContext? context, out NtStatus status)
    {
        context = null;
        status = DokanResult.Success;
        LocalFile file;
        lock (_localFilesLock)
        {
            if (!_localFiles.TryGetValue(NormalizePath(path), out file!))
                return false;

            if (mode == FileMode.CreateNew)
            {
                status = DokanResult.FileExists;
                return true;
            }

            if (file.Kind == LocalFileKind.Pending
                && (wantsWrite || mode is FileMode.Create or FileMode.OpenOrCreate or FileMode.Truncate))
            {
                status = DokanResult.AccessDenied;
                return true;
            }

            file.OpenCount++;
            file.ActiveCount++;
        }

        try
        {
            var mutatesFile = wantsWrite || mode is FileMode.Create or FileMode.OpenOrCreate or FileMode.Truncate;
            if (mutatesFile)
            {
                var pendingDenied = false;
                var missingRegistration = false;
                var deleteAfterRejectedOpen = false;
                using (AcquireFileSync(file, info))
                {
                    lock (file.DataSync)
                    {
                        lock (_localFilesLock)
                        {
                            var registered = _localFiles.TryGetValue(NormalizePath(path), out var current)
                                             && ReferenceEquals(current, file);
                            if (file.Kind == LocalFileKind.Pending)
                            {
                                file.OpenCount--;
                                file.ActiveCount--;
                                pendingDenied = true;
                                deleteAfterRejectedOpen = file.OpenCount == 0 && file.DeleteOnClose;
                            }
                            else if (!registered)
                            {
                                file.OpenCount--;
                                file.ActiveCount--;
                                missingRegistration = true;
                                deleteAfterRejectedOpen = file.OpenCount == 0 && file.DeleteOnClose;
                            }
                        }

                        if (!pendingDenied && !missingRegistration && wantsWrite)
                        {
                            if (mode is FileMode.Create or FileMode.Truncate)
                            {
                                var stream = file.Hydrated
                                    ? EnsureStreamOpen(file, writable: true)
                                    : CreateEmptyStream(file);
                                stream.SetLength(0);
                                MarkModified(file);
                            }
                            else if (file.Hydrated)
                                EnsureStreamOpen(file, writable: true);
                        }
                    }
                }

                if (deleteAfterRejectedOpen)
                    DeleteClosedLocalFile(file);
                if (pendingDenied)
                {
                    status = DokanResult.AccessDenied;
                    return true;
                }
                if (missingRegistration)
                    return false;
            }

            lock (_localFilesLock)
                if (_localFiles.TryGetValue(NormalizePath(path), out var registered)
                    && ReferenceEquals(registered, file) && file.Kind != LocalFileKind.Pending)
                    file.DeleteOnClose = false;
            context = new HandleContext { File = file };
            return true;
        }
        catch
        {
            lock (_localFilesLock)
            {
                file.OpenCount--;
                file.ActiveCount--;
            }
            throw;
        }
    }

    private bool TryOpenPendingByPath(string path, FileMode mode, bool wantsWrite,
        out HandleContext? context, out NtStatus status)
    {
        context = null;
        status = DokanResult.Success;
        lock (_localFilesLock)
            if (_pendingFiles.Count == 0)
                return false;
        var parent = _gateway.ResolveParentDirectory(path);
        if (parent is null)
            return false;

        LocalFile file;
        lock (_localFilesLock)
        {
            if (!_pendingFiles.TryGetValue(PendingKey(parent.Value.DirectoryId, parent.Value.Name), out file!))
                return false;

            if (mode == FileMode.CreateNew)
            {
                status = DokanResult.FileExists;
                return true;
            }

            if (wantsWrite || mode is FileMode.Create or FileMode.OpenOrCreate or FileMode.Truncate)
            {
                status = DokanResult.AccessDenied;
                return true;
            }

            file.OpenCount++;
            file.ActiveCount++;
        }

        try
        {
            lock (file.DataSync)
            {
                using var stream = new FileStream(file.TempPath, FileMode.Open,
                    System.IO.FileAccess.Read, FileShare.ReadWrite);
            }
            context = new HandleContext { File = file };
            return true;
        }
        catch
        {
            lock (_localFilesLock)
            {
                file.OpenCount--;
                file.ActiveCount--;
            }
            throw;
        }
    }

    private bool TryOpenCandidate(LocalFile candidate, FileMode mode, IDokanFileInfo info, out HandleContext? context,
        out bool alreadyExists, out NtStatus status)
    {
        context = null;
        status = DokanResult.Success;
        LocalFile file;
        var added = false;
        var conflict = false;
        lock (_localFilesLock)
        {
            if (_localFiles.TryGetValue(candidate.Path, out file!))
            {
                alreadyExists = true;
                if (file.Kind == LocalFileKind.Pending)
                {
                    status = mode == FileMode.CreateNew ? DokanResult.FileExists : DokanResult.AccessDenied;
                    conflict = true;
                }
                else if (mode == FileMode.CreateNew)
                {
                    status = DokanResult.FileExists;
                    conflict = true;
                }
            }
            else
            {
                _localFiles.Add(candidate.Path, candidate);
                file = candidate;
                added = true;
                alreadyExists = false;
            }

            if (!conflict)
            {
                file.OpenCount++;
                file.ActiveCount++;
            }
        }

        if (conflict)
        {
            DisposeCandidate(candidate);
            return false;
        }
        if (!added)
            DisposeCandidate(candidate);

        try
        {
            using (AcquireFileSync(file, info))
            lock (file.DataSync)
            {
                if (file.Hydrated)
                    EnsureStreamOpen(file, writable: true);
                if (alreadyExists && (mode is FileMode.Create or FileMode.Truncate))
                {
                    var stream = file.Hydrated
                        ? EnsureStreamOpen(file, writable: true)
                        : CreateEmptyStream(file);
                    stream.SetLength(0);
                    MarkModified(file);
                }
            }

            lock (_localFilesLock)
                file.DeleteOnClose = false;
            context = new HandleContext { File = file };
            return true;
        }
        catch
        {
            lock (_localFilesLock)
            {
                file.OpenCount--;
                file.ActiveCount--;
                if (added)
                    RemoveLocalFileLocked(file);
            }
            if (added)
                DisposeCandidate(candidate);
            throw;
        }
    }

    private LocalFile OpenNew(string path, string directoryId, string name)
    {
        var temp = Path.Combine(_writeDir, Guid.NewGuid().ToString("N"));
        try
        {
            // FileShare.Read позволяет загрузке открыть рабочую копию для чтения.
            var stream = new FileStream(temp, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.Read);
            return new LocalFile
            {
                Path = NormalizePath(path),
                Name = name,
                DirectoryId = directoryId,
                TempPath = temp,
                Stream = stream,
                Kind = LocalFileKind.Writable,
                Hydrated = true,
            };
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private LocalFile OpenExistingForWrite(string path, ResolvedNode node, bool truncate)
    {
        var temp = Path.Combine(_writeDir, Guid.NewGuid().ToString("N"));
        try
        {
            var stream = truncate
                ? new FileStream(temp, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.Read)
                : null;
            return new LocalFile
            {
                Path = NormalizePath(path),
                Name = node.Name,
                DirectoryId = node.DirectoryId,
                TempPath = temp,
                Stream = stream,
                ExistingEntryId = node.EntryId,
                ExistingNode = node,
                Kind = LocalFileKind.Writable,
                Modified = truncate,
                Hydrated = truncate,
            };
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private void Persist(LocalFile file, CancellationToken cancellationToken = default)
    {
        try
        {
            PersistCore(file, cancellationToken);
        }
        catch
        {
            file.KeepData = true;
            throw;
        }
    }

    private void PersistCore(LocalFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = EnsureStreamOpen(file, writable: true);
        lock (file.DataSync)
            stream.Flush();
        if (stream.Length == 0)
        {
            if (file.ExistingEntryId is null && file.Op?.Stage != PersistStage.Done)
            {
                lock (_localFilesLock)
                    file.Kind = LocalFileKind.Placeholder;
                file.Modified = false;
            }
            else
            {
                LastSyncError = Loc.T("Eng_EmptyFileNotSupported");
                file.Modified = false;
            }
            file.DirtyAfterPersist = false;
            return;
        }

        var operation = file.Op;
        if (operation is null || operation.Stage == PersistStage.Done)
        {
            operation = new PersistOperation();
            file.Op = operation;
        }

        if (operation.Stage is PersistStage.Upload or PersistStage.Processing)
        {
            if (operation.Stage == PersistStage.Processing && operation.Upload2 is null)
                throw new InvalidDataException(Loc.T("Eng_UploadSessionInvalidResponse"));

            operation.FileId = _gateway.UploadResumableAsync(operation, file.TempPath, file.Name,
                () => SaveOutboxState(file), TimeSpan.FromMinutes(2),
                cancellationToken).GetAwaiter().GetResult();

            operation.Stage = file.ExistingEntryId is null && operation.SupersededFileId is null
                ? PersistStage.Attach : PersistStage.DeleteOld;
            SaveOutboxState(file);
        }

        if (operation.Stage == PersistStage.DeleteOld)
        {
            if (operation.SupersededFileId is { } supersededFileId)
            {
                var superseded = _gateway.FindFileEntryByFileId(file.DirectoryId, supersededFileId, cancellationToken);
                if (superseded is not null)
                {
                    file.ExistingEntryId = superseded.Entry.Id;
                    operation.OldEntryDeleted = false;
                }
                operation.SupersededFileId = null;
                SaveOutboxState(file);
            }

            if (!operation.OldEntryDeleted && file.ExistingEntryId is { } oldEntryId)
            {
                try
                {
                    _gateway.DeleteFileEntry(oldEntryId, cancellationToken);
                }
                catch (RpcException ex) when (ServerErrors.HasCode(ex, ServerErrors.FileEntryNotFound))
                {
                    file.KeepData = true;
                    // Запись уже удалена, в том числе при потере ответа предыдущего вызова.
                }

                operation.OldEntryDeleted = true;
                SaveOutboxState(file);
            }

            operation.Stage = PersistStage.Attach;
            SaveOutboxState(file);
        }

        if (operation.Stage == PersistStage.Attach)
        {
            try
            {
                var attached = AttachUploadedFile(file, operation, cancellationToken);
                file.ExistingEntryId = attached.Entry.Id;
                file.ExistingNode = new ResolvedNode
                {
                    DirectoryId = attached.Entry.DirectoryId,
                    EntryId = attached.Entry.Id,
                    FileId = attached.File.Id,
                    Name = attached.Entry.Name,
                    Length = attached.File.FileSize,
                    Created = ToDate(attached.Entry.CreatedAt),
                    Updated = ToDate(attached.File.UploadedAt),
                };
                SaveOutboxState(file);
            }
            catch (RpcException ex) when (IsBusinessError(ex))
            {
                file.KeepData = true;
                RestoreOldEntry(file, operation, ex);
                throw;
            }
        }

        operation.Stage = PersistStage.Done;
        SaveOutboxState(file);
        _gateway.InvalidateListing(file.DirectoryId);
        file.Modified = false;
        file.DirtyAfterPersist = false;
        file.KeepData = false;
        lock (_localFilesLock)
            if (file.Kind == LocalFileKind.Placeholder)
                file.Kind = LocalFileKind.Writable;
        ClearLastSyncErrorIfNoPendingUploads();
        EngineLog.Info($"Сохранён файл «{file.Name}» (fileId={operation.FileId})");
    }

    private FileEntryDetailed AttachUploadedFile(LocalFile file, PersistOperation operation,
        CancellationToken cancellationToken)
    {
        var fileId = operation.FileId!;
        FileEntryDetailed? attached = null;
        if (operation.AttachAttempted || operation.AttachCompleted)
        {
            attached = _gateway.FindFileEntryByFileId(file.DirectoryId, fileId, cancellationToken);
            if (attached is not null)
                operation.AttachCompleted = true;
        }

        if (operation.AttachCompleted && attached is null)
        {
            EngineLog.Info(Loc.T("Eng_AttachedFileRetryFmt", file.Name));
            operation.AttachCompleted = false;
            SaveOutboxState(file);
        }

        if (!operation.AttachCompleted)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isUploadRetry = operation.AttachAttempted;
                    operation.AttachAttempted = true;
                    SaveOutboxState(file);
                    if (operation.Upload2?.SessionId is { } uploadSessionId)
                        _gateway.AttachFile(file.DirectoryId, fileId, file.Name, cancellationToken,
                            uploadSessionId, isUploadRetry);
                    else
                        _gateway.AttachFile(file.DirectoryId, fileId, file.Name, cancellationToken);
                    operation.AttachCompleted = true;
                }
                catch (RpcException ex) when (ServerErrors.HasCode(ex, ServerErrors.FileAlreadyAttached))
                {
                    file.KeepData = true;
                    attached = _gateway.FindFileEntryByFileId(file.DirectoryId, fileId, cancellationToken);
                    if (attached is not null)
                    {
                        operation.AttachCompleted = true;
                        break;
                    }

                    throw;
                }
                catch (Exception ex) when (IsUncertainAttachResult(ex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    file.KeepData = true;
                    attached = _gateway.FindFileEntryByFileId(file.DirectoryId, fileId, cancellationToken);
                    if (attached is not null)
                    {
                        operation.AttachCompleted = true;
                        break;
                    }

                    if (attempt == 1)
                        throw;

                    continue;
                }

                break;
            }
        }

        attached ??= _gateway.FindFileEntryByFileId(file.DirectoryId, fileId, cancellationToken);
        if (attached is null)
            throw new InvalidOperationException(Loc.T("Eng_AttachedFileNotListedFmt", file.Name));

        operation.AttachCompleted = true;
        SaveOutboxState(file);
        return attached;
    }

    private void RestoreOldEntry(LocalFile file, PersistOperation operation, RpcException originalError)
    {
        if (!operation.OldEntryDeleted || file.ExistingEntryId is not { } oldEntryId)
            return;

        try
        {
            _gateway.RestoreFromTrash(oldEntryId, file.DirectoryId, CancellationToken.None);
            operation.OldEntryDeleted = false;
            operation.AttachAttempted = false;
            operation.AttachCompleted = false;
            operation.Stage = PersistStage.DeleteOld;
            SaveOutboxState(file);
        }
        catch (Exception rollbackError)
        {
            throw new AggregateException(originalError, rollbackError);
        }
    }

    private static bool IsBusinessError(RpcException exception)
        => exception.StatusCode == StatusCode.FailedPrecondition && ServerErrors.GetCode(exception) is not null;

    internal static bool IsUncertainMoveResult(Exception exception)
        => exception is not RpcException rpc || !IsBusinessError(rpc);

    internal static bool IsUncertainAttachResult(Exception exception)
        => exception switch
        {
            RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded } => true,
            TimeoutException or HttpRequestException or IOException or OperationCanceledException => true,
            _ => false,
        };

    private static bool IsExpectedMoveRollbackError(Exception exception)
        => exception is RpcException rpc
           && (ServerErrors.HasCode(rpc, ServerErrors.FileEntryNotFound)
               || ServerErrors.HasCode(rpc, ServerErrors.DirectoryNotFound)
               || ServerErrors.HasCode(rpc, ServerErrors.DirectoryNameConflict)
               || ServerErrors.HasCode(rpc, ServerErrors.FileAlreadyAttached));

    private void ClearLastSyncErrorIfNoPendingUploads()
    {
        if (_outbox is not null && _outbox.Count == 0)
            LastSyncError = _preservedPlaceholderError;
    }

    private void SaveOutboxState(LocalFile file)
    {
        if (_outbox is null || file.OutboxId is not { } id)
            return;

        var entry = _outbox.Find(id);
        if (entry is null)
            return;

        entry.DataPath = file.TempPath;
        entry.ExistingEntryId = file.ExistingEntryId;
        entry.Op = CopyOperation(file.Op ?? new PersistOperation());
        entry.Stage = entry.Op.Stage;
        entry.FileId = entry.Op.FileId;
        _outbox.Save(entry);
    }

    internal Task ProcessOutboxEntryAsync(UploadOutboxEntry entry)
        => ProcessOutboxEntryAsync(entry, CancellationToken.None);

    internal async Task ProcessOutboxEntryAsync(UploadOutboxEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentEntry = _outbox?.Find(entry.Id);
        if (currentEntry is null)
            return;
        entry = currentEntry;

        LocalFile? file;
        lock (_localFilesLock)
        {
            file = _pendingFiles.Values.Concat(_openOutboxFiles.Values)
                .FirstOrDefault(candidate => candidate.OutboxId == entry.Id);
            _openOutboxFiles.Remove(entry.Id);
        }

        if (file is null)
        {
            RegisterOutboxPending(entry);
            lock (_localFilesLock)
                file = _pendingFiles.GetValueOrDefault(PendingKey(entry.DirectoryId, entry.Name));
        }
        else
            RegisterOutboxPending(entry, file);

        if (file is null)
            throw new InvalidOperationException(Loc.T("Eng_OutboxRegisterFailedFmt", entry.Name));

        lock (file.Sync)
        {
            file.TempPath = entry.DataPath;
            file.ExistingEntryId = entry.ExistingEntryId;
            file.Op = CopyOperation(entry.Op ?? new PersistOperation { Stage = entry.Stage, FileId = entry.FileId });
            file.KeepData = true;
            try
            {
                if (file.Op.Stage != PersistStage.Done)
                    Persist(file, cancellationToken);
            }
            finally
            {
                lock (file.DataSync)
                {
                    file.Stream?.Dispose();
                    file.Stream = null;
                }
            }
        }

        await Task.CompletedTask;
    }

    internal static PersistOperation CopyOperation(PersistOperation operation) => new()
    {
        Stage = operation.Stage,
        FileId = operation.FileId,
        Upload2 = operation.Upload2?.Copy(),
        OldEntryDeleted = operation.OldEntryDeleted,
        AttachAttempted = operation.AttachAttempted,
        AttachCompleted = operation.AttachCompleted,
        SupersededFileId = operation.SupersededFileId,
    };

    private void CloseLocalFile(string fileName, LocalFile file, IDokanFileInfo info)
    {
        bool isPending;
        bool deletePendingData;
        lock (_localFilesLock)
        {
            if (file.OpenCount > 0)
                file.OpenCount--;
            if (file.OpenCount != 0)
                return;
            if (file.OutboxId is { } outboxId && _openOutboxFiles.TryGetValue(outboxId, out var openFile)
                && ReferenceEquals(openFile, file))
                _openOutboxFiles.Remove(outboxId);
            isPending = file.Kind == LocalFileKind.Pending;
            deletePendingData = isPending && file.DeleteOnClose;
            if (deletePendingData)
                RemoveLocalFileLocked(file);
        }

        if (isPending)
        {
            if (deletePendingData)
                lock (file.DataSync)
                    TryDelete(file.TempPath);
            return;
        }

        using (AcquireFileSync(file, info))
        {
            bool shouldPersist;
            lock (_localFilesLock)
                shouldPersist = file.OpenCount == 0 && file.Modified
                                && file.Kind != LocalFileKind.Pending
                                && !file.DeleteOnClose && file.ActiveCount == 0
                                && file.Op?.Stage != PersistStage.Processing;
            if (shouldPersist)
            {
                try
                {
                    using var keepAlive = DokanKeepAlive.Start(info, _time);
                    Persist(file);
                }
                catch (UploadProcessingPendingException ex)
                {
                    file.KeepData = true;
                    EngineLog.Info(ex.Message);
                }
                catch (Exception ex)
                {
                    file.KeepData = true;
                    EngineLog.Error($"CloseFile(\"{fileName}\")", ex);
                    LastSyncError = Loc.T("Eng_SyncFailedFmt", Path.GetFileName(fileName), ConnectionErrors.GetDisplayMessage(ex));
                }
            }

            var length = file.Stream?.Length ?? (File.Exists(file.TempPath) ? new FileInfo(file.TempPath).Length : 0);
            bool keep;
            lock (_localFilesLock)
            {
                if (file.OpenCount != 0)
                    return;
                if (file.ExistingEntryId is null && file.Op?.Stage != PersistStage.Done && length == 0
                    && !file.DeleteOnClose && !file.KeepData)
                    file.Kind = LocalFileKind.Placeholder;
                keep = !file.DeleteOnClose
                       && (file.Kind is LocalFileKind.Placeholder or LocalFileKind.Pending || file.KeepData);
                if (!keep)
                    RemoveLocalFileLocked(file);
            }

            lock (file.DataSync)
            {
                file.Stream?.Dispose();
                file.Stream = null;
            }

            if (keep && !file.DeleteOnClose
                && file.Kind != LocalFileKind.Pending && file.KeepData
                && (file.Modified || file.Op is { Stage: not PersistStage.Done }) && _outbox is not null)
            {
                try
                {
                    UploadOutboxEntry entry;
                    lock (file.DataSync)
                        entry = _outbox.Add(file, _serverKey, _ownerUserId());
                    if (entry.OwnerUserId is not null)
                        RegisterOutboxPending(entry, file);
                    else
                        lock (_localFilesLock)
                            RemoveLocalFileLocked(file);
                    try { OutboxAdded?.Invoke(); }
                    catch (Exception callbackError) { EngineLog.Error(Loc.T("Eng_OutboxTriggerFailed"), callbackError); }
                }
                catch (Exception ex)
                {
                    file.KeepData = true;
                    EngineLog.Error(Loc.T("Eng_OutboxTransferFailedFmt", file.Name), ex);
                    LastSyncError = Loc.T("Eng_SyncFailedFmt", file.Name, ConnectionErrors.GetDisplayMessage(ex));
                }
            }

            if (!keep)
                lock (file.DataSync)
                    TryDelete(file.TempPath);
        }
    }

    private void DeleteLocalFile(LocalFile file)
    {
        if (file.ExistingEntryId is { } entryId)
        {
            try { _gateway.QueueDeleteFileEntry(entryId, file.DirectoryId); }
            catch (Exception ex) { RecordDeleteError(file, ex); }
        }

        var operation = file.Op;
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        if (operation is { Stage: PersistStage.Upload, Upload2.SessionId: { } uploadSessionId })
            CancelUploadSessionInBackground(uploadSessionId);

        if (operation is { AttachAttempted: true, FileId: { } attemptedFileId })
            fileIds.Add(attemptedFileId);
        if (operation?.SupersededFileId is { } supersededFileId)
            fileIds.Add(supersededFileId);

        foreach (var fileId in fileIds)
        {
            try
            {
                var attached = _gateway.FindFileEntryByFileId(file.DirectoryId, fileId);
                if (attached is not null)
                    _gateway.QueueDeleteFileEntry(attached.Entry.Id, file.DirectoryId);
            }
            catch (Exception ex)
            {
                RecordDeleteError(file, ex);
            }
        }

        lock (_localFilesLock)
            RemoveLocalFileLocked(file);
    }

    private void RecordDeleteError(LocalFile file, Exception exception)
    {
        EngineLog.Error($"DeleteLocalFile(\"{file.Name}\")", exception);
        LastSyncError = Loc.T("Eng_SyncFailedFmt", file.Name, ConnectionErrors.GetDisplayMessage(exception));
    }

    private void RemoveLocalFileLocked(LocalFile file)
    {
        if (_localFiles.TryGetValue(file.Path, out var registered) && ReferenceEquals(registered, file))
            _localFiles.Remove(file.Path);

        var key = PendingKey(file.DirectoryId, file.Name);
        if (_pendingFiles.TryGetValue(key, out var pending) && ReferenceEquals(pending, file))
            _pendingFiles.Remove(key);

        if (file.OutboxId is { } id && _openOutboxFiles.TryGetValue(id, out var openFile)
            && ReferenceEquals(openFile, file))
            _openOutboxFiles.Remove(id);
    }

    private void DisposeCandidate(LocalFile file)
    {
        lock (file.DataSync)
        {
            try { file.Stream?.Dispose(); } catch { /* ignore */ }
            TryDelete(file.TempPath);
        }
    }

    private void DeleteClosedLocalFile(LocalFile file)
    {
        lock (file.DataSync)
        {
            file.Stream?.Dispose();
            file.Stream = null;
            TryDelete(file.TempPath);
        }
    }

    private static FileStream EnsureStreamOpen(LocalFile file, bool writable)
    {
        if (file.Stream is { CanRead: true } stream && (!writable || stream.CanWrite))
            return stream;

        file.Stream?.Dispose();
        file.Stream = new FileStream(file.TempPath, FileMode.Open,
            writable ? System.IO.FileAccess.ReadWrite : System.IO.FileAccess.Read,
            writable ? FileShare.Read : FileShare.ReadWrite);
        return file.Stream;
    }

    private static int ReadLocalCopy(LocalFile file, byte[] buffer, long offset)
    {
        lock (file.DataSync)
        {
            using var stream = new FileStream(file.TempPath, FileMode.Open,
                System.IO.FileAccess.Read, FileShare.ReadWrite);
            if (offset >= stream.Length)
                return 0;
            stream.Position = offset;
            return stream.Read(buffer, 0, buffer.Length);
        }
    }

    private void EnsureHydrated(LocalFile file, IDokanFileInfo info)
    {
        ResolvedNode? node;
        lock (file.DataSync)
        {
            if (file.Hydrated)
                return;
            file.Stream?.Dispose();
            file.Stream = null;
            node = file.ExistingNode;
        }

        if (node is null)
        {
            lock (file.DataSync)
            {
                if (file.Hydrated)
                    return;
                file.Stream = new FileStream(file.TempPath, FileMode.OpenOrCreate,
                    System.IO.FileAccess.ReadWrite, FileShare.Read);
                file.Hydrated = true;
            }
            return;
        }

        using var keepAlive = DokanKeepAlive.Start(info, _time);
        _gateway.DownloadToAsync(node.FileId, file.TempPath, node.Length).GetAwaiter().GetResult();
        lock (file.DataSync)
        {
            file.Stream = new FileStream(file.TempPath, FileMode.Open,
                System.IO.FileAccess.ReadWrite, FileShare.Read);
            file.Hydrated = true;
        }
    }

    private static FileStream CreateEmptyStream(LocalFile file)
    {
        file.Stream?.Dispose();
        file.Stream = new FileStream(file.TempPath, FileMode.Create,
            System.IO.FileAccess.ReadWrite, FileShare.Read);
        file.Hydrated = true;
        return file.Stream;
    }

    private void TruncateLocalFileToZero(LocalFile file)
    {
        lock (file.DataSync)
        {
            if (LocalFileLengthLocked(file) == 0)
                return;
            var stream = file.Hydrated
                ? EnsureStreamOpen(file, writable: true)
                : CreateEmptyStream(file);
            stream.SetLength(0);
            MarkModified(file);
        }
    }

    private bool IsRegisteredReadableLocalFile(LocalFile file, string entryId)
    {
        lock (_localFilesLock)
        {
            var registered = (!string.IsNullOrEmpty(file.Path)
                              && _localFiles.TryGetValue(file.Path, out var current)
                              && ReferenceEquals(current, file))
                             || (_pendingFiles.TryGetValue(PendingKey(file.DirectoryId, file.Name), out var pending)
                                 && ReferenceEquals(pending, file));
            return registered
                   && string.Equals(file.ExistingEntryId, entryId, StringComparison.Ordinal)
                   && (file.Hydrated || file.OpenCount > 0 || file.Kind == LocalFileKind.Pending);
        }
    }

    private IDisposable AcquireFileSync(LocalFile file, IDokanFileInfo info)
    {
        if (Monitor.TryEnter(file.Sync))
            return new FileSyncLease(file.Sync);

        using var keepAlive = DokanKeepAlive.Start(info, _time);
        Monitor.Enter(file.Sync);
        return new FileSyncLease(file.Sync);
    }

    private static long LocalFileLengthLocked(LocalFile file)
    {
        if (!file.Hydrated && file.ExistingNode is { } node)
            return node.Length;
        return file.Stream?.Length ?? (File.Exists(file.TempPath) ? new FileInfo(file.TempPath).Length : 0);
    }

    private void MarkModified(LocalFile file)
    {
        file.Stream?.Flush();
        file.Modified = true;
        if (file.Op?.Stage == PersistStage.Done)
            file.DirtyAfterPersist = true;
        else if (file.Op is { } operation && (operation.FileId is not null || operation.Upload2 is not null))
        {
            // Потерянный ответ Attach мог скрывать уже созданную запись этой версии.
            if (operation.AttachAttempted)
                operation.SupersededFileId = operation.FileId;
            operation.FileId = null;
            if (operation.Stage == PersistStage.Upload && operation.Upload2?.SessionId is { } abandonedSessionId)
                CancelUploadSessionInBackground(abandonedSessionId);
            operation.Upload2 = null;
            operation.Stage = PersistStage.Upload;
            operation.AttachAttempted = false;
            operation.AttachCompleted = false;
        }
    }

    private void CancelUploadSessionInBackground(string sessionId)
    {
        UploadCancellationTaskForTesting = Task.Run(() =>
        {
            try { _gateway.CancelUploadSession(sessionId); }
            catch (RpcException ex) when (ServerErrors.HasCode(ex, ServerErrors.UploadSessionState)
                                          || ServerErrors.HasCode(ex, ServerErrors.UploadSessionNotFound))
            {
                EngineLog.Info($"Сессия загрузки {sessionId} уже отсутствует или не находится в состоянии UPLOADING.");
            }
            catch (Exception ex) { EngineLog.Error(Loc.T("Eng_UploadCancelFailed"), ex); }
        });
    }

    private LocalFile? FindLocalFile(string path)
    {
        lock (_localFilesLock)
            return _localFiles.GetValueOrDefault(NormalizePath(path));
    }

    private LocalFile? FindPendingFileByPath(string path)
    {
        lock (_localFilesLock)
            if (_pendingFiles.Count == 0)
                return null;
        var parent = _gateway.ResolveParentDirectory(path);
        if (parent is null)
            return null;

        lock (_localFilesLock)
            return _pendingFiles.GetValueOrDefault(PendingKey(parent.Value.DirectoryId, parent.Value.Name));
    }

    private LocalFile? FindLocalFileByEntryId(string entryId)
    {
        lock (_localFilesLock)
            return _localFiles.Values.Concat(_pendingFiles.Values)
                .FirstOrDefault(file => file.ExistingEntryId == entryId);
    }

    private List<LocalFile> GetLocalFiles()
    {
        lock (_localFilesLock)
            return _localFiles.Values.Concat(_pendingFiles.Values).Distinct().ToList();
    }

    private static FileInformation GetLocalFileInformation(LocalFile file)
    {
        long length;
        DateTime created;
        DateTime updated;
        lock (file.DataSync)
        {
            length = LocalFileLengthLocked(file);
            created = file.ExistingNode?.Created ?? file.CreatedAt;
            updated = file.ExistingNode?.Updated ?? file.CreatedAt;
        }
        return new FileInformation
        {
            FileName = file.Name,
            Attributes = FileAttributes.Normal,
            Length = length,
            CreationTime = created,
            LastWriteTime = updated,
            LastAccessTime = updated,
        };
    }

    private static string NormalizePath(string path)
        => "\\" + path.Replace('/', '\\').Trim('\\');

    private static string PendingKey(string directoryId, string name) => directoryId + "\0" + name;

    private static NtStatus ExistingStatus(FileMode mode)
        => mode is FileMode.Create or FileMode.OpenOrCreate ? DokanResult.AlreadyExists : DokanResult.Success;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private void DeleteNode(string fileName)
    {
        var node = SafeResolve(fileName);
        if (node is null)
            return;

        var parent = _gateway.ResolveParentDirectory(fileName);

        if (node.IsDirectory)
        {
            _gateway.FlushPending();
            _gateway.InvalidateListing(node.DirectoryId);
            if (DirectoryIsNotEmpty(fileName, node))
            {
                var reason = Loc.T("Eng_DirectoryNotEmpty");
                LastSyncError = Loc.T("Eng_DirectoryDeleteFailedFmt", Path.GetFileName(fileName), reason);
                EngineLog.Error($"Cleanup(\"{fileName}\")", new IOException(reason));
                return;
            }

            _gateway.DeleteDirectory(node.DirectoryId);
            _gateway.InvalidateListing(node.DirectoryId);
        }
        else
        {
            _gateway.QueueDeleteFileEntry(node.EntryId, node.DirectoryId);
        }

        if (parent != null)
            _gateway.InvalidateListing(parent.Value.DirectoryId);
    }

    private bool DirectoryIsNotEmpty(string path, ResolvedNode node)
    {
        var normalizedPath = NormalizePath(path).TrimEnd('\\');
        var childPathPrefix = normalizedPath.Length == 0 ? "\\" : normalizedPath + "\\";
        lock (_localFilesLock)
            if (_localFiles.Values.Concat(_pendingFiles.Values).Distinct().Any(file =>
                string.Equals(file.DirectoryId, node.DirectoryId, StringComparison.Ordinal)
                || !string.IsNullOrEmpty(file.Path)
                && file.Path.StartsWith(childPathPrefix, StringComparison.OrdinalIgnoreCase)))
                return true;

        if (_outbox?.Entries.Any(entry => entry.ServerKey == _serverKey
                && entry.DirectoryId == node.DirectoryId) == true)
            return true;

        var listing = _gateway.ListDirectory(node.DirectoryId);
        return listing.Subdirs.Count > 0 || listing.Files.Count > 0;
    }

    internal static NtStatus ToStatus(Exception exception)
    {
        if (exception is RpcException rpc)
        {
            if (ServerErrors.HasCode(rpc, ServerErrors.FileEntryNotFound))
                return DokanResult.FileNotFound;
            if (ServerErrors.HasCode(rpc, ServerErrors.DirectoryNotFound))
                return DokanResult.PathNotFound;
            if (ServerErrors.HasCode(rpc, ServerErrors.DirectoryNameConflict))
                return DokanResult.FileExists;
            if (rpc.StatusCode == StatusCode.PermissionDenied
                || ServerErrors.HasCode(rpc, ServerErrors.CloudAccessDenied)
                || ServerErrors.HasCode(rpc, ServerErrors.CircularMove))
                return DokanResult.AccessDenied;
        }

        if (exception is IOException io)
        {
            var hresult = unchecked((uint)io.HResult);
            if (hresult is 0x80070070 or 0x80070027)
                return DokanResult.DiskFull;
        }

        return UnexpectedNetworkError;
    }

    private ResolvedNode? SafeResolve(string path) => _gateway.Resolve(path);

    private static DateTime? ToDate(Timestamp? t) => t?.ToDateTime();
}
