using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Services;

public interface ITrashPurgeService
{
    /// <summary>
    /// Окончательно удаляет переданные записи корзины. Возвращает число физически удалённых
    /// из S3 блобов (оригиналов + превью).
    /// </summary>
    Task<int> PurgeEntriesAsync(IReadOnlyCollection<CloudFileEntry> entries, CancellationToken cancellationToken);

    /// <summary>
    /// Физически удаляет осиротевшие блобы (с пустым списком Uploaders) среди переданных
    /// кандидатов и связанные с ними превью. Возвращает число физически удалённых блобов.
    /// </summary>
    Task<int> PurgeOrphanBlobsAsync(IReadOnlyCollection<Guid> candidateFileIds, CancellationToken cancellationToken);
}
