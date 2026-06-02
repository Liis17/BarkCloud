using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeFolderShare;

/// <summary>
/// Отзывает публичность папки. Каскадно снимает и отдельные публичные ссылки (<see cref="Domain.ShareLink"/>)
/// на все файлы поддерева — даже на те, что были опубликованы по отдельности (папка снова приватна).
/// </summary>
public class RevokeFolderShareCommandHandler : IRequestHandler<RevokeFolderShareCommand, CloudEmpty>
{
    private readonly IFolderShareStorage _folderShares;
    private readonly IShareStorage _shares;
    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly UserContext _userContext;
    private readonly ILogger<RevokeFolderShareCommandHandler> _logger;

    public RevokeFolderShareCommandHandler(
        IFolderShareStorage folderShares,
        IShareStorage shares,
        ICloudHierarchyStorage hierarchy,
        UserContext userContext,
        ILogger<RevokeFolderShareCommandHandler> logger)
    {
        _folderShares = folderShares;
        _shares = shares;
        _hierarchy = hierarchy;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RevokeFolderShareCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var share = await _folderShares.GetById(ownerId, request.FolderShareId, cancellationToken);
        if (share is null)
            return new CloudEmpty(); // идемпотентно

        // Каскад: снять все отдельные публичные ссылки на файлы поддерева расшаренной папки.
        var subtree = await _hierarchy.GetSubtree(ownerId, share.DirectoryId, cancellationToken);
        var subtreeIds = subtree.Select(d => d.Id).ToList();
        var entries = await _hierarchy.GetFileEntriesInDirectories(ownerId, subtreeIds, cancellationToken);
        var fileIds = entries.Select(e => e.FileId).Distinct().ToList();

        var removedLinks = await _shares.RemoveByFiles(ownerId, fileIds, cancellationToken);
        await _folderShares.Remove(ownerId, request.FolderShareId, cancellationToken);

        _logger.LogInformation(
            "Отозвана публичная папка {ShareId} (dir {DirectoryId}); снято публичных ссылок файлов: {Links} (Owner: {OwnerId})",
            share.Id, share.DirectoryId, removedLinks, ownerId);

        return new CloudEmpty();
    }
}
