using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface ITempFilesStorage
{
    Task<TempFile> CreateTempFile(Guid fileId);
    /// <param name="notAfter">Не позже этого момента (срок публичной ссылки); <c>null</c> — обычный срок temp-ссылки.</param>
    Task<List<TempFile>> CreateTempFilesBatchAsync(IEnumerable<Guid> fileIds, DateTime? notAfter = null, CancellationToken cancellationToken = default);
    Task<TempFile?> GetTempFile(Guid tempFileId);
    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default);
}
