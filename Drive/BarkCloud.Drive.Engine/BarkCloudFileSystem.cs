using System.Security.AccessControl;

using BarkCloud.Proto.Files;

using DokanNet;

using Google.Protobuf.WellKnownTypes;

using FileAccess = DokanNet.FileAccess; // в IDokanOperations access — это DokanNet.FileAccess

namespace BarkCloud.Drive.Engine;

// Read-only проекция облака BarkCloud на файловую систему Dokany.
// Том монтируется с DokanOptions.WriteProtection, поэтому все мутирующие
// колбэки сведены к отказам — их Windows и так не вызовет для записи.
internal sealed class BarkCloudFileSystem(CloudGateway gateway) : IDokanOperations
{
    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        // read-only: любая попытка создать/обрезать/дописать запрещена
        if (mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate or FileMode.Append)
            return DokanResult.AccessDenied;

        var node = SafeResolve(fileName);
        if (node is null)
            return DokanResult.FileNotFound;

        if (node.IsDirectory)
        {
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        if (info.IsDirectory) // ожидали папку, а это файл
            return DokanResult.NotADirectory;

        const FileAccess writeBits = FileAccess.WriteData | FileAccess.AppendData
            | FileAccess.Delete | FileAccess.GenericWrite | FileAccess.GenericAll;
        if ((access & writeBits) != 0)
            return DokanResult.AccessDenied;

        return DokanResult.Success; // открытие на чтение; байты не качаем здесь
    }

    public void Cleanup(string fileName, IDokanFileInfo info) { }

    public void CloseFile(string fileName, IDokanFileInfo info) => info.Context = null;

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        var node = SafeResolve(fileName);
        if (node is null || node.IsDirectory)
            return DokanResult.FileNotFound;

        try
        {
            bytesRead = gateway.Read(node.FileId, buffer, offset);
            return DokanResult.Success;
        }
        catch
        {
            return DokanResult.Error;
        }
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
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
        try { listing = gateway.ListDirectory(node.DirectoryId); }
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
        return DokanResult.NotImplemented; // → Dokan сам отфильтрует результат FindFiles
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        freeBytesAvailable = totalNumberOfBytes = totalNumberOfFreeBytes = 0;
        try
        {
            var s = gateway.GetStorage();
            var free = Math.Max(0, s.StorageLimit - s.TotalUsedStorage);
            totalNumberOfBytes = s.StorageLimit;
            freeBytesAvailable = free;
            totalNumberOfFreeBytes = free;
            return DokanResult.Success;
        }
        catch
        {
            return DokanResult.Error;
        }
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = "BarkCloud";
        fileSystemName = "BarkCloudFS";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames
                   | FileSystemFeatures.UnicodeOnDisk
                   | FileSystemFeatures.ReadOnlyVolume;
        return DokanResult.Success;
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus Unmounted(IDokanFileInfo info) => DokanResult.Success;

    // ───────── read-only: всё мутирующее отклоняется ─────────

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        return DokanResult.AccessDenied;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        => DokanResult.AccessDenied;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime, IDokanFileInfo info) => DokanResult.AccessDenied;

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info) => DokanResult.AccessDenied;

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info) => DokanResult.AccessDenied;

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        => DokanResult.AccessDenied;

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info) => DokanResult.AccessDenied;

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) => DokanResult.AccessDenied;

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info) => DokanResult.AccessDenied;

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = new List<FileInformation>();
        return DokanResult.NotImplemented;
    }

    private ResolvedNode? SafeResolve(string path)
    {
        try { return gateway.Resolve(path); }
        catch { return null; }
    }

    private static DateTime? ToDate(Timestamp? t) => t?.ToDateTime();
}
