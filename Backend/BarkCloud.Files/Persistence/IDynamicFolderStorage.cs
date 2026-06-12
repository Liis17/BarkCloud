using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Persistence;

public sealed record DuplicateFileItem(UploadFile File, string GroupKey);

public interface IDynamicFolderStorage
{
    Task<DynamicFolder?> GetFolder(Guid id, CancellationToken cancellationToken = default);
    Task<bool> FolderNameExists(long ownerId, string name, CancellationToken cancellationToken = default);
    Task<DynamicFolder> AddFolder(DynamicFolder folder, CancellationToken cancellationToken = default);
    Task UpdateFolder(DynamicFolder folder, CancellationToken cancellationToken = default);
    Task RemoveFolder(DynamicFolder folder, CancellationToken cancellationToken = default);

    /// <summary>Пользовательские папки владельца (системные добавляет хэндлер), по (SortOrder, CreatedAt).</summary>
    Task<List<DynamicFolder>> ListFolders(long ownerId, CancellationToken cancellationToken = default);

    /// <summary>Максимальный SortOrder у владельца (-1 если папок нет) — для размещения новой в конце.</summary>
    Task<int> GetMaxSortOrder(long ownerId, CancellationToken cancellationToken = default);

    /// <summary>Количество файлов, удовлетворяющих критериям (для бейджа плитки).</summary>
    Task<int> CountByCriteria(long ownerId, DynamicFolderCriteria criteria, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Страница содержимого по критериям с cursor-пагинацией (limit+1 для hasMore).</summary>
    Task<List<UploadFile>> ListItemsPage(
        long ownerId, DynamicFolderCriteria criteria, DateTime now,
        DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Самый свежий файл по критериям — кандидат на обложку.</summary>
    Task<UploadFile?> GetFirstItem(long ownerId, DynamicFolderCriteria criteria, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Количество живых файлов владельца, входящих в duplicate-группы по SHA256 (mediaOnly: фото/видео, иначе документы/аудио/прочее).</summary>
    Task<int> CountDuplicateItems(long ownerId, bool mediaOnly, CancellationToken cancellationToken = default);

    /// <summary>Страница живых файлов владельца, входящих в duplicate-группы по SHA256 (mediaOnly: фото/видео, иначе документы/аудио/прочее).</summary>
    Task<List<DuplicateFileItem>> ListDuplicateItemsPage(
        long ownerId, bool mediaOnly,
        DateTime? cursorCreatedAt, Guid? cursorFileId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Самый свежий файл из duplicate-групп — кандидат на обложку (mediaOnly: фото/видео, иначе документы/аудио/прочее).</summary>
    Task<UploadFile?> GetFirstDuplicateItem(long ownerId, bool mediaOnly, CancellationToken cancellationToken = default);
}
