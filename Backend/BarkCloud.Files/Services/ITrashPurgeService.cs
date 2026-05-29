using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Services;

public interface ITrashPurgeService
{
    /// <summary>
    /// Окончательно удаляет переданные записи корзины. Возвращает число физически удалённых
    /// из S3 блобов (оригиналов + превью).
    /// </summary>
    Task<int> PurgeEntriesAsync(IReadOnlyCollection<CloudFileEntry> entries, CancellationToken cancellationToken);
}
