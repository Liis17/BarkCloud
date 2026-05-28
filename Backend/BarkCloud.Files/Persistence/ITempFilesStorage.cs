using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface ITempFilesStorage
{
    Task<TempFile> CreateTempFile(Guid fileId);
    Task<List<TempFile>> CreateTempFilesBatchAsync(IEnumerable<Guid> fileIds, CancellationToken cancellationToken = default);
    Task<TempFile?> GetTempFile(Guid tempFileId);
    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default);
}
