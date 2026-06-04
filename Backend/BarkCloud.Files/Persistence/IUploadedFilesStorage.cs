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

    /// <summary>
    /// Фото/видео владельца, снятые в указанный день (месяц+день <see cref="FileMetadata.TakenAt"/>) в любые годы,
    /// от новых к старым. Для «Воспоминаний» («В этот день»). Исключает превью и файлы в корзине.
    /// </summary>
    Task<List<MemoryMediaItem>> ListMemoriesForDay(long ownerId, int month, int day, int maxTotal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Страница фото/видео владельца с GPS-координатами (<see cref="FileMetadata.Latitude"/>/<see cref="FileMetadata.Longitude"/>),
    /// от новых к старым с cursor-пагинацией. Для карты. Исключает превью и файлы в корзине.
    /// </summary>
    Task<List<LocatedMediaItem>> ListMediaWithLocationPage(long ownerId, DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Блоб + его дата съёмки (для группировки «Воспоминаний» по годам).</summary>
public sealed record MemoryMediaItem(UploadFile File, DateTime TakenAt);

/// <summary>Блоб + его GPS-координаты и дата съёмки (для точек на карте).</summary>
public sealed record LocatedMediaItem(UploadFile File, double Latitude, double Longitude, DateTime? TakenAt);
