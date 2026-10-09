using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

internal static class CloudTreeLocks
{
    /// <summary>
    /// Занимает деревья владельцев в текущей транзакции до чтения и изменения ссылок на файлы.
    /// Все замки владельцев берутся до блокировок строк, по возрастанию OwnerId.
    /// </summary>
    public static async Task LockCloudTreesAsync(
        this FilesContext context, IEnumerable<long> ownerIds, CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Блокировка деревьев возможна только внутри транзакции.");

        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
            return;

        foreach (var ownerId in ownerIds.Distinct().Order())
        {
            var key = $"cloud-tree:{ownerId}";
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
        }
    }
}
