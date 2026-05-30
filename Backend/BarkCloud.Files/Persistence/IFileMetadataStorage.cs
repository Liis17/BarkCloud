using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IFileMetadataStorage
{
    /// <summary>Метаданные блоба или null, если не сохранены.</summary>
    Task<FileMetadata?> Get(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>Идемпотентное добавление: если запись для FileId уже есть — не перезаписывает.</summary>
    Task AddIfMissing(FileMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Страница идентификаторов <see cref="UploadFile"/>, для которых метаданные ещё не сохранены.
    /// Курсор по <see cref="UploadFile.Id"/> в возрастающем порядке. Используется бэкафиллом.
    /// </summary>
    Task<List<Guid>> ListFilesMissingMetadata(
        Guid? cursorFileId,
        int limit,
        CancellationToken cancellationToken = default);
}
