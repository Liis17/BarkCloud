using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Shared.Exceptions.Files;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Services;

public static class DirectoryVisibility
{
    /// <summary>Пересчитывает итоговую видимость папки и всех её потомков сверху вниз.</summary>
    public static async Task RecomputeSubtreeAsync(
        ICloudHierarchyStorage storage,
        long ownerId,
        CloudDirectory root,
        CancellationToken cancellationToken = default)
    {
        var parentHideFromGallery = false;
        var parentHideFromSearch = false;
        if (root.ParentId is { } parentId)
        {
            var parent = await storage.GetDirectoryAsNoTracking(parentId, cancellationToken);
            if (parent is null)
                throw new DirectoryNotFoundException();

            parentHideFromGallery = parent.EffectiveHideFromGallery;
            parentHideFromSearch = parent.EffectiveHideFromSearch;
        }

        var subtree = await storage.GetSubtree(ownerId, root.Id, cancellationToken);
        if (root.ParentId is { } rootParentId && subtree.Any(directory => directory.Id == rootParentId))
            throw new CircularMoveException();

        var effectiveById = new Dictionary<Guid, (bool HideFromGallery, bool HideFromSearch)>();
        foreach (var directory in subtree)
        {
            if (directory.InheritVisibility && directory.ParentId is { } directoryParentId)
            {
                var inherited = directory.Id == root.Id
                    ? (parentHideFromGallery, parentHideFromSearch)
                    : effectiveById[directoryParentId];
                directory.EffectiveHideFromGallery = inherited.Item1;
                directory.EffectiveHideFromSearch = inherited.Item2;
            }
            else
            {
                directory.EffectiveHideFromGallery = directory.HideFromGallery;
                directory.EffectiveHideFromSearch = directory.HideFromSearch;
            }

            effectiveById[directory.Id] = (directory.EffectiveHideFromGallery, directory.EffectiveHideFromSearch);
        }
    }
}
