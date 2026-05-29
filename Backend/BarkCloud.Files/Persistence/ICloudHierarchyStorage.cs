using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface ICloudHierarchyStorage
{
    Task<CloudDirectory?> GetDirectory(Guid id, CancellationToken cancellationToken = default);
    Task<CloudDirectory?> GetDirectoryAsNoTracking(Guid id, CancellationToken cancellationToken = default);
    Task<bool> DirectoryNameExists(long ownerId, Guid? parentId, string name, CancellationToken cancellationToken = default);
    Task<CloudDirectory> AddDirectory(CloudDirectory directory, CancellationToken cancellationToken = default);
    Task UpdateDirectory(CloudDirectory directory, CancellationToken cancellationToken = default);
    Task<List<CloudDirectory>> ListSubdirectories(long ownerId, Guid? parentId, CancellationToken cancellationToken = default);
    Task<List<CloudDirectory>> GetSubtree(long ownerId, Guid rootDirectoryId, CancellationToken cancellationToken = default);
    void RemoveDirectories(IEnumerable<CloudDirectory> directories);
    Task<CloudFileEntry?> GetFileEntry(Guid id, CancellationToken cancellationToken = default);
    Task<bool> FileEntryNameExists(long ownerId, Guid directoryId, string name, CancellationToken cancellationToken = default);
    Task<bool> FileEntryExistsForFile(long ownerId, Guid fileId, CancellationToken cancellationToken = default);
    Task<CloudFileEntry> AddFileEntry(CloudFileEntry entry, CancellationToken cancellationToken = default);
    Task UpdateFileEntry(CloudFileEntry entry, CancellationToken cancellationToken = default);
    Task RemoveFileEntry(CloudFileEntry entry, CancellationToken cancellationToken = default);
    Task<List<CloudFileEntry>> ListFilesInDirectory(long ownerId, Guid directoryId, CancellationToken cancellationToken = default);
    Task<List<CloudFileEntry>> GetFileEntriesInDirectories(long ownerId, IReadOnlyCollection<Guid> directoryIds, CancellationToken cancellationToken = default);
    void RemoveFileEntries(IEnumerable<CloudFileEntry> entries);
    Task<CloudFileEntry?> GetTrashedEntry(Guid id, CancellationToken cancellationToken = default);
    Task<List<CloudFileEntry>> ListTrashedPage(long ownerId, DateTime? cursorDeletedAt, Guid? cursorEntryId, int limit, CancellationToken cancellationToken = default);
    Task<List<CloudFileEntry>> GetAllTrashedEntries(long ownerId, CancellationToken cancellationToken = default);
    Task<List<CloudFileEntry>> GetExpiredTrashedEntries(DateTime now, int batchSize, CancellationToken cancellationToken = default);
    Task<HashSet<Guid>> GetEffectivelyTrashedFileIds(long ownerId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
