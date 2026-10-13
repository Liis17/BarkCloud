using BarkCloud.Files.Persistence;
using BarkCloud.Files.Features.Cloud.GetDirectoryProperties;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.SetDirectoryVisibility;

public class SetDirectoryVisibilityCommandHandler : IRequestHandler<SetDirectoryVisibilityCommand, DirectoryProperties>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;

    public SetDirectoryVisibilityCommandHandler(ICloudHierarchyStorage storage, UserContext userContext)
    {
        _storage = storage;
        _userContext = userContext;
    }

    public async Task<DirectoryProperties> Handle(SetDirectoryVisibilityCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        await using var treeLock = await _storage.LockTree(ownerId, cancellationToken);

        var directory = await _storage.GetDirectory(request.DirectoryId, cancellationToken);
        if (directory is null)
            throw new DirectoryNotFoundException();
        if (directory.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        directory.InheritVisibility = directory.ParentId is null
            ? !(request.HideFromGallery || request.HideFromSearch)
            : request.Inherit;
        directory.HideFromGallery = directory.InheritVisibility ? false : request.HideFromGallery;
        directory.HideFromSearch = directory.InheritVisibility ? false : request.HideFromSearch;

        await DirectoryVisibility.RecomputeSubtreeAsync(_storage, ownerId, directory, cancellationToken);
        await _storage.SaveChangesAsync(cancellationToken);

        var subtree = await _storage.GetSubtree(ownerId, directory.Id, cancellationToken);
        var directoryIds = subtree.Select(x => x.Id).ToArray();
        var (fileCount, totalSizeBytes) = await _storage.GetLiveFileStatsInDirectories(ownerId, directoryIds, cancellationToken);
        await treeLock.CommitAsync(cancellationToken);

        return GetDirectoryPropertiesCommandHandler.ToResponse(
            directory, fileCount, Math.Max(0, subtree.Count - 1), totalSizeBytes);
    }
}
