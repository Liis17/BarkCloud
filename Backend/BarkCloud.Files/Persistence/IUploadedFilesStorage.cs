using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IUploadedFilesStorage
{
    Task<UploadFile> AddToStorage(UploadFile file);
    Task UpdateFile(UploadFile file);
    Task AddUploaderToFile(Guid fileId, long userId);
    Task<UploadFile?> GetFile(Guid id);
    Task<List<UploadFile>> GetFiles(List<Guid> ids);
    Task DeleteFile(Guid fileId);
    Task<UploadFile?> GetOriginalByPreviewFileId(Guid previewFileId, CancellationToken cancellationToken = default);
    Task<List<FilePreview>> GetPreviewsForFile(Guid originalFileId, CancellationToken cancellationToken = default);
    Task<Dictionary<Guid, List<FilePreview>>> GetPreviewsForFiles(IEnumerable<Guid> originalFileIds, CancellationToken cancellationToken = default);
    Task RemoveUploaderFromFile(Guid fileId, long userId, CancellationToken cancellationToken = default);
    Task<long> GetUserStorageUsed(long userId);
    Task<Dictionary<UploadFileType, long>> GetUserStorageByType(long userId);
    Task<bool> IsPreviewFile(Guid fileId, CancellationToken cancellationToken = default);
    Task<List<UploadFile>> ListUserMediaPage(long ownerId, MediaKind kind, DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default);
    Task<List<UploadFile>> ListUserImagesPage(long ownerId, DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default);
    Task RemovePreviewsForOriginal(Guid originalFileId, long ownerId, CancellationToken cancellationToken = default);
}
