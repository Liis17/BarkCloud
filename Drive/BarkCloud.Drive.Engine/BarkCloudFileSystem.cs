using System.Security.AccessControl;

using BarkCloud.Proto.Files;

using DokanNet;

using Google.Protobuf.WellKnownTypes;

using FileAccess = DokanNet.FileAccess; // в IDokanOperations access — это DokanNet.FileAccess

namespace BarkCloud.Drive.Engine;

// Запись открытого хэндла: байты буферизуются в локальную рабочую копию, а на Cleanup
// загружаются в облако и привязываются (или заменяют существующую запись).
internal sealed class WriteSession : IDisposable
{
    public required string Name;
    public required string DirectoryId;      // папка, куда привязать
    public required string TempPath;
    public required FileStream Stream;
    public string? ExistingEntryId;          // != null → редактирование существующего файла
    public string OriginalFileId = "";
    public readonly object Sync = new();
    public bool Modified;
    public bool Persisted;

    public bool IsNew => ExistingEntryId is null;

    public void Dispose()
    {
        try { Stream.Dispose(); } catch { /* ignore */ }
        try { File.Delete(TempPath); } catch { /* ignore */ }
    }
}

// Read-write проекция облака BarkCloud на файловую систему Dokany.
internal sealed class BarkCloudFileSystem : IDokanOperations
{
    private const FileAccess WriteBits =
        FileAccess.WriteData | FileAccess.AppendData | FileAccess.GenericWrite | FileAccess.GenericAll;

    private readonly CloudGateway _gateway;
    private readonly string _writeDir;

    public BarkCloudFileSystem(CloudGateway gateway)
    {
        _gateway = gateway;
        _writeDir = Path.Combine(Path.GetTempPath(), "BarkCloudDrive", "write");
        Directory.CreateDirectory(_writeDir);
    }

    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        var node = SafeResolve(fileName);

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
                catch { return DokanResult.Error; }
            }

            return DokanResult.PathNotFound;
        }

        // путь оказался папкой — открываем как папку
        if (node is { IsDirectory: true })
        {
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        var wantsWrite = mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate or FileMode.Append
                         || (access & WriteBits) != 0;

        // ───────── существующий файл ─────────
        if (node != null)
        {
            if (mode == FileMode.CreateNew)
                return DokanResult.FileExists;

            if (!wantsWrite)
                return DokanResult.Success; // чтение — контекст не нужен

            try
            {
                info.Context = OpenExistingForWrite(node, truncate: mode is FileMode.Create or FileMode.Truncate);
                return DokanResult.Success;
            }
            catch { return DokanResult.Error; }
        }

        // ───────── новый файл ─────────
        if (mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate)
        {
            var parent = _gateway.ResolveParentDirectory(fileName);
            if (parent is null)
                return DokanResult.PathNotFound;
            try
            {
                info.Context = OpenNew(parent.Value.DirectoryId, parent.Value.Name);
                return DokanResult.Success;
            }
            catch { return DokanResult.Error; }
        }

        return DokanResult.FileNotFound;
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        try
        {
            if (info.DeletePending)
                DeleteNode(fileName);
            else if (info.Context is WriteSession { Persisted: false } ws && (ws.IsNew || ws.Modified))
                PersistSession(ws);
        }
        catch
        {
            // не удалось сохранить/удалить — проглатываем (диск не должен падать)
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        if (info.Context is WriteSession ws)
            ws.Dispose();
        info.Context = null;
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;

        if (info.Context is WriteSession ws)
        {
            lock (ws.Sync)
            {
                if (offset >= ws.Stream.Length)
                    return DokanResult.Success;
                ws.Stream.Position = offset;
                bytesRead = ws.Stream.Read(buffer, 0, buffer.Length);
            }
            return DokanResult.Success;
        }

        var node = SafeResolve(fileName);
        if (node is null || node.IsDirectory)
            return DokanResult.FileNotFound;

        try
        {
            bytesRead = _gateway.Read(node.FileId, buffer, offset);
            return DokanResult.Success;
        }
        catch { return DokanResult.Error; }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        if (info.Context is not WriteSession ws)
            return DokanResult.AccessDenied;

        lock (ws.Sync)
        {
            ws.Stream.Position = info.WriteToEndOfFile ? ws.Stream.Length : offset;
            ws.Stream.Write(buffer, 0, buffer.Length);
            ws.Modified = true;
        }
        bytesWritten = buffer.Length;
        return DokanResult.Success;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        if (info.Context is WriteSession ws)
            lock (ws.Sync) ws.Stream.Flush();
        return DokanResult.Success;
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        if (info.Context is WriteSession ws)
        {
            long length;
            lock (ws.Sync) length = ws.Stream.Length;
            fileInfo = new FileInformation { FileName = ws.Name, Attributes = FileAttributes.Normal, Length = length };
            return DokanResult.Success;
        }

        fileInfo = default;
        var node = SafeResolve(fileName);
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

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        var node = SafeResolve(fileName);
        if (node is null || !node.IsDirectory)
            return DokanResult.FileNotFound;

        DirectoryListingDetailed listing;
        try { listing = _gateway.ListDirectory(node.DirectoryId); }
        catch { return DokanResult.Error; }

        foreach (var d in listing.Subdirs)
            files.Add(new FileInformation
            {
                FileName = d.Name,
                Attributes = FileAttributes.Directory,
                CreationTime = ToDate(d.CreatedAt),
                LastWriteTime = ToDate(d.UpdatedAt),
                LastAccessTime = ToDate(d.UpdatedAt),
                Length = 0,
            });

        foreach (var f in listing.Files)
            files.Add(new FileInformation
            {
                FileName = f.Entry.Name,
                Attributes = FileAttributes.Normal,
                CreationTime = ToDate(f.Entry.CreatedAt),
                LastWriteTime = ToDate(f.File.UploadedAt),
                LastAccessTime = ToDate(f.File.UploadedAt),
                Length = f.File.FileSize,
            });

        return DokanResult.Success;
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern,
        out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        return DokanResult.NotImplemented; // → Dokan отфильтрует результат FindFiles
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (info.Context is not WriteSession ws)
            return DokanResult.AccessDenied;
        lock (ws.Sync) { ws.Stream.SetLength(length); ws.Modified = true; }
        return DokanResult.Success;
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        if (info.Context is not WriteSession ws)
            return DokanResult.AccessDenied;
        lock (ws.Sync)
        {
            if (length < ws.Stream.Length)
                ws.Stream.SetLength(length);
        }
        return DokanResult.Success;
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        var node = SafeResolve(oldName);
        if (node is null)
            return DokanResult.FileNotFound;

        var from = _gateway.ResolveParentDirectory(oldName);
        var to = _gateway.ResolveParentDirectory(newName);
        if (from is null || to is null)
            return DokanResult.PathNotFound;

        var parentChanged = !string.Equals(from.Value.DirectoryId, to.Value.DirectoryId, StringComparison.Ordinal);
        var nameChanged = !string.Equals(from.Value.Name, to.Value.Name, StringComparison.Ordinal);

        try
        {
            if (node.IsDirectory)
            {
                if (parentChanged) _gateway.MoveDirectory(node.DirectoryId, to.Value.DirectoryId);
                if (nameChanged) _gateway.RenameDirectory(node.DirectoryId, to.Value.Name);
            }
            else
            {
                if (parentChanged) _gateway.MoveFileEntry(node.EntryId, to.Value.DirectoryId);
                if (nameChanged) _gateway.RenameFileEntry(node.EntryId, to.Value.Name);
            }

            _gateway.InvalidateListing(from.Value.DirectoryId);
            _gateway.InvalidateListing(to.Value.DirectoryId);
            return DokanResult.Success;
        }
        catch { return DokanResult.Error; }
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        var node = SafeResolve(fileName);
        if (node is null || !node.IsDirectory)
            return DokanResult.Success;

        try
        {
            var listing = _gateway.ListDirectory(node.DirectoryId);
            if (listing.Subdirs.Count > 0 || listing.Files.Count > 0)
                return DokanResult.DirectoryNotEmpty;
        }
        catch { /* при ошибке листинга разрешаем — сервер удаляет рекурсивно */ }

        return DokanResult.Success;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        freeBytesAvailable = totalNumberOfBytes = totalNumberOfFreeBytes = 0;
        try
        {
            var s = _gateway.GetStorage();
            var free = Math.Max(0, s.StorageLimit - s.TotalUsedStorage);
            totalNumberOfBytes = s.StorageLimit;
            freeBytesAvailable = free;
            totalNumberOfFreeBytes = free;
            return DokanResult.Success;
        }
        catch { return DokanResult.Error; }
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = "BarkCloud";
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

    private WriteSession OpenNew(string directoryId, string name)
    {
        var temp = Path.Combine(_writeDir, Guid.NewGuid().ToString("N"));
        var stream = new FileStream(temp, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.None);
        return new WriteSession { Name = name, DirectoryId = directoryId, TempPath = temp, Stream = stream };
    }

    private WriteSession OpenExistingForWrite(ResolvedNode node, bool truncate)
    {
        var temp = Path.Combine(_writeDir, Guid.NewGuid().ToString("N"));
        if (!truncate)
            _gateway.DownloadToAsync(node.FileId, temp).GetAwaiter().GetResult(); // гидрация для частичных правок

        var stream = new FileStream(temp, FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None);
        return new WriteSession
        {
            Name = node.Name,
            DirectoryId = node.DirectoryId, // для файла — родительская папка
            TempPath = temp,
            Stream = stream,
            ExistingEntryId = node.EntryId,
            OriginalFileId = node.FileId,
            Modified = truncate,
        };
    }

    private void PersistSession(WriteSession ws)
    {
        lock (ws.Sync) ws.Stream.Flush();

        var (url, _) = _gateway.GetUploadUrl();
        var fileId = _gateway.UploadAsync(url, ws.Name, ws.TempPath).GetAwaiter().GetResult();

        if (ws.ExistingEntryId is null)
        {
            _gateway.AttachFile(ws.DirectoryId, fileId, ws.Name);
        }
        else if (!string.Equals(fileId, ws.OriginalFileId, StringComparison.Ordinal))
        {
            // содержимое изменилось → новая запись вместо старой
            _gateway.AttachFile(ws.DirectoryId, fileId, ws.Name);
            _gateway.DeleteFileEntry(ws.ExistingEntryId);
        }
        // fileId == OriginalFileId → содержимое не изменилось, ничего не делаем

        _gateway.InvalidateListing(ws.DirectoryId);
        ws.Persisted = true;
    }

    private void DeleteNode(string fileName)
    {
        var node = SafeResolve(fileName);
        if (node is null)
            return;

        var parent = _gateway.ResolveParentDirectory(fileName);

        if (node.IsDirectory)
        {
            _gateway.DeleteDirectory(node.DirectoryId);
            _gateway.InvalidateListing(node.DirectoryId);
        }
        else
        {
            _gateway.DeleteFileEntry(node.EntryId);
        }

        if (parent != null)
            _gateway.InvalidateListing(parent.Value.DirectoryId);
    }

    private ResolvedNode? SafeResolve(string path)
    {
        try { return _gateway.Resolve(path); }
        catch { return null; }
    }

    private static DateTime? ToDate(Timestamp? t) => t?.ToDateTime();
}
