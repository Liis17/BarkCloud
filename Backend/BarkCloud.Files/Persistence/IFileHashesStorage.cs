using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IFileHashesStorage
{
    Task AddHash(FileHash fileHash);
    Task<Guid?> GetFileIdByHash(string hash);
    Task<List<Guid>> GetFileIdsByHash(string hash, CancellationToken cancellationToken = default);
    Task<bool> HashExists(string hash);
    Task<HashSet<string>> GetExistingHashes(IReadOnlyCollection<string> hashes);
    Task<FileHash?> GetHashByFileId(Guid fileId);
    Task<int> DeleteHashByFileId(Guid fileId, CancellationToken cancellationToken = default);
}
