namespace BarkCloud.Files.Persistence;

/// <summary>
/// Эксклюзивный доступ к структуре дерева папок владельца: транзакция БД с advisory-блокировкой.
/// Без <see cref="CommitAsync"/> при освобождении изменения откатываются, блокировка снимается.
/// </summary>
public interface ICloudTreeLock : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}
