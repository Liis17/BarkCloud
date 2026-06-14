using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public interface IFileMetadataStorage
{
    /// <summary>Метаданные блоба или null, если не сохранены.</summary>
    Task<FileMetadata?> Get(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>Пакетное чтение метаданных для набора файлов (для галереи). Без записей — ключа в словаре нет.</summary>
    Task<Dictionary<Guid, FileMetadata>> GetForFiles(
        IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Страница id видеофайлов, у которых метаданные есть, но HDR-признак ещё не зондировался
    /// (<see cref="FileMetadata.IsHdr"/> == null). Курсор по id. Используется HDR-бэкафиллом.
    /// </summary>
    Task<List<Guid>> ListVideosMissingHdr(
        Guid? cursorFileId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Проставляет HDR-признак существующей записи метаданных (true/false — всегда определён).</summary>
    Task SetHdr(Guid fileId, bool isHdr, CancellationToken cancellationToken = default);

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
