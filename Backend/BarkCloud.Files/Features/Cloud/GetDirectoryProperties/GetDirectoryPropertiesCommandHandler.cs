using BarkCloud.Files.Persistence;
using BarkCloud.Files.Domain;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;
using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.GetDirectoryProperties;

public class GetDirectoryPropertiesCommandHandler : IRequestHandler<GetDirectoryPropertiesCommand, DirectoryProperties>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;

    public GetDirectoryPropertiesCommandHandler(ICloudHierarchyStorage storage, UserContext userContext)
    {
        _storage = storage;
        _userContext = userContext;
    }

    public async Task<DirectoryProperties> Handle(GetDirectoryPropertiesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var directory = await _storage.GetDirectoryAsNoTracking(request.DirectoryId, cancellationToken);
        if (directory is null)
            throw new DirectoryNotFoundException();
        if (directory.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        var subtree = await _storage.GetSubtree(ownerId, directory.Id, cancellationToken);
        var directoryIds = subtree.Select(x => x.Id).ToArray();
        var (fileCount, totalSizeBytes) = await _storage.GetLiveFileStatsInDirectories(ownerId, directoryIds, cancellationToken);

        return ToResponse(directory, fileCount, Math.Max(0, subtree.Count - 1), totalSizeBytes);
    }

    internal static DirectoryProperties ToResponse(CloudDirectory directory, long fileCount, long folderCount, long totalSizeBytes) =>
        new()
        {
            Directory = new DirectoryInfo
            {
                Id = directory.Id.ToString(),
                ParentId = directory.ParentId?.ToString() ?? string.Empty,
                Name = directory.Name,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(directory.CreatedAt, DateTimeKind.Utc)),
                UpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(directory.UpdatedAt, DateTimeKind.Utc))
            },
            FileCount = fileCount,
            FolderCount = folderCount,
            TotalSizeBytes = totalSizeBytes,
            Inherit = directory.InheritVisibility,
            HideFromGallery = directory.HideFromGallery,
            HideFromSearch = directory.HideFromSearch,
            EffectiveHideFromGallery = directory.EffectiveHideFromGallery,
            EffectiveHideFromSearch = directory.EffectiveHideFromSearch,
            HasParent = directory.ParentId.HasValue
        };
}
