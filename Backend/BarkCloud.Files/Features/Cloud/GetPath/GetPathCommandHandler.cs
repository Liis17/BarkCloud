using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.GetPath;

public class GetPathCommandHandler : IRequestHandler<GetPathCommand, PathResponse>
{
    private readonly CloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<GetPathCommandHandler> _logger;

    public GetPathCommandHandler(
        CloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<GetPathCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<PathResponse> Handle(GetPathCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        Guid? startDirectoryId;
        string? leafName = null;

        if (request.EntryId.HasValue)
        {
            var entry = await _storage.GetFileEntry(request.EntryId.Value, cancellationToken);
            if (entry is null)
                throw new FileEntryNotFoundException();
            if (entry.OwnerId != ownerId)
                throw new CloudAccessDeniedException();

            leafName = entry.Name;
            startDirectoryId = entry.DirectoryId == CloudHierarchyStorage.RootDirectoryId
                ? (Guid?)null
                : entry.DirectoryId;
        }
        else if (request.DirectoryId.HasValue)
        {
            var dir = await _storage.GetDirectoryAsNoTracking(request.DirectoryId.Value, cancellationToken);
            if (dir is null)
                throw new DirectoryNotFoundException();
            if (dir.OwnerId != ownerId)
                throw new CloudAccessDeniedException();

            leafName = dir.Name;
            startDirectoryId = dir.ParentId;
        }
        else
        {
            // Корень
            return new PathResponse { FullPath = "/" };
        }

        // Поднимаемся вверх, собирая сегменты предков
        var ancestors = new List<(Guid Id, string Name)>();
        var cursorId = startDirectoryId;
        while (cursorId.HasValue)
        {
            var cur = await _storage.GetDirectoryAsNoTracking(cursorId.Value, cancellationToken);
            if (cur is null)
                break;
            if (cur.OwnerId != ownerId)
                throw new CloudAccessDeniedException();

            ancestors.Add((cur.Id, cur.Name));
            cursorId = cur.ParentId;
        }

        ancestors.Reverse();

        var response = new PathResponse();
        foreach (var seg in ancestors)
        {
            response.Segments.Add(new PathSegment
            {
                Id = seg.Id.ToString(),
                Name = seg.Name
            });
        }

        var parts = ancestors.Select(s => s.Name).ToList();
        parts.Add(leafName ?? string.Empty);
        response.FullPath = "/" + string.Join("/", parts);

        return response;
    }
}
