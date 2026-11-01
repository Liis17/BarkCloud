using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

/// <summary>Живая запись владельца с хешем: блоб и признак его готовности (READY).</summary>
public sealed record OwnerHashMatch(string Hash, Guid FileId, bool IsReady);

public interface IFileHashesStorage
{
    Task AddHash(FileHash fileHash);
    Task<Guid?> GetFileIdByHash(string hash, string? storageProfileId = null);
    Task<List<Guid>> GetFileIdsByHash(string hash, CancellationToken cancellationToken = default);
    Task<bool> HashExists(string hash);
    Task<List<OwnerHashMatch>> GetLiveHashMatchesForOwner(long ownerId, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken = default);
    Task<FileHash?> GetHashByFileId(Guid fileId);
    Task<int> DeleteHashByFileId(Guid fileId, CancellationToken cancellationToken = default);
}
