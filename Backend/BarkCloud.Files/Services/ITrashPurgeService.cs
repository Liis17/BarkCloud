using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Services;

/// <summary>
/// Итог окончательной зачистки: сколько записей корзины реально удалено и сколько блобов
/// (оригиналов + превью) физически удалено из S3.
/// </summary>
public readonly record struct TrashPurgeResult(int Entries, int Blobs);

public interface ITrashPurgeService
{
    /// <summary>
    /// Окончательно удаляет переданные записи корзины (ручное «Удалить навсегда» / «Очистить
    /// корзину»). Записи, которые к моменту удаления уже не в корзине (например, параллельно
    /// восстановлены), не трогаются и в результат не попадают.
    /// </summary>
    Task<TrashPurgeResult> PurgeEntriesAsync(IReadOnlyCollection<CloudFileEntry> entries, CancellationToken cancellationToken);

    /// <summary>
    /// Окончательно удаляет переданные записи корзины, у которых срок хранения истёк к моменту
    /// <paramref name="now"/> (фоновый воркер). Запись, восстановленная или повторно удалённая с
    /// более поздним PurgeAt после выборки, не трогается.
    /// </summary>
    Task<TrashPurgeResult> PurgeExpiredEntriesAsync(IReadOnlyCollection<CloudFileEntry> entries, DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Физически удаляет осиротевшие блобы (с пустым списком Uploaders) среди переданных
    /// кандидатов и связанные с ними превью. Возвращает число физически удалённых блобов.
    /// </summary>
    Task<int> PurgeOrphanBlobsAsync(IReadOnlyCollection<Guid> candidateFileIds, CancellationToken cancellationToken);
}
