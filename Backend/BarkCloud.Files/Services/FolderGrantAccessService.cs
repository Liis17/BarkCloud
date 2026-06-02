using BarkCloud.Files.Persistence;

namespace BarkCloud.Files.Services;

/// <summary>
/// Разрешение доступа получателя к папкам/файлам через гранты на папку (<see cref="Domain.DirectoryGrant"/>).
/// Грант покрывает всё поддерево папки рекурсивно (включая файлы, добавленные позже).
/// </summary>
public class FolderGrantAccessService
{
    private readonly IDirectoryGrantStorage _dirGrants;
    private readonly ICloudHierarchyStorage _hierarchy;

    public FolderGrantAccessService(IDirectoryGrantStorage dirGrants, ICloudHierarchyStorage hierarchy)
    {
        _dirGrants = dirGrants;
        _hierarchy = hierarchy;
    }

    /// <summary>
    /// Если у получателя есть грант, поддерево которого содержит <paramref name="directoryId"/>,
    /// возвращает владельца этой папки (для листинга в его иерархии); иначе null.
    /// </summary>
    public async Task<long?> ResolveAccessibleDirectoryOwner(long recipientId, Guid directoryId, CancellationToken cancellationToken = default)
    {
        var grants = await _dirGrants.ListByRecipient(recipientId, cancellationToken);
        foreach (var g in grants)
        {
            var subtree = await _hierarchy.GetSubtree(g.OwnerId, g.DirectoryId, cancellationToken);
            if (subtree.Any(d => d.Id == directoryId))
                return g.OwnerId;
        }
        return null;
    }

    /// <summary>
    /// Доступен ли получателю файл через какой-либо грант на папку: у владельца-дарителя есть
    /// живая запись файла в директории, входящей в поддерево гранта.
    /// </summary>
    public async Task<bool> RecipientCanAccessFileViaFolder(long recipientId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var grants = await _dirGrants.ListByRecipient(recipientId, cancellationToken);
        foreach (var g in grants)
        {
            var entries = await _hierarchy.GetLiveEntriesForFile(g.OwnerId, fileId, cancellationToken);
            if (entries.Count == 0)
                continue;

            var subtreeIds = (await _hierarchy.GetSubtree(g.OwnerId, g.DirectoryId, cancellationToken))
                .Select(d => d.Id)
                .ToHashSet();

            if (entries.Any(e => subtreeIds.Contains(e.DirectoryId)))
                return true;
        }
        return false;
    }
}
