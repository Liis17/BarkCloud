using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.GetSubdirectorySizes;

public class GetSubdirectorySizesCommandHandler : IRequestHandler<GetSubdirectorySizesCommand, GetSubdirectorySizesResponse>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;

    public GetSubdirectorySizesCommandHandler(ICloudHierarchyStorage storage, UserContext userContext)
    {
        _storage = storage;
        _userContext = userContext;
    }

    public async Task<GetSubdirectorySizesResponse> Handle(GetSubdirectorySizesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        if (request.ParentId is Guid parentId)
        {
            var parent = await _storage.GetDirectoryAsNoTracking(parentId, cancellationToken);
            if (parent is null)
                throw new DirectoryNotFoundException();
            if (parent.OwnerId != ownerId)
                throw new CloudAccessDeniedException();
        }

        // Версии в папках читаются до пересчёта (см. ICloudHierarchyStorage.GetSubtreeSizes).
        var children = await _storage.ListSubdirectories(ownerId, request.ParentId, cancellationToken);
        var sizes = await _storage.GetSubtreeSizes(ownerId, children, cancellationToken);

        var response = new GetSubdirectorySizesResponse();
        foreach (var child in children)
        {
            response.Sizes.Add(new SubdirectorySizeEntry
            {
                DirectoryId = child.Id.ToString(),
                SizeBytes = sizes[child.Id]
            });
        }
        return response;
    }
}
