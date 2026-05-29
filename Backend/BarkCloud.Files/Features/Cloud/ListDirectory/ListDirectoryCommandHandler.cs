using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;
using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.ListDirectory;

public class ListDirectoryCommandHandler : IRequestHandler<ListDirectoryCommand, DirectoryListing>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<ListDirectoryCommandHandler> _logger;

    public ListDirectoryCommandHandler(
        ICloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<ListDirectoryCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<DirectoryListing> Handle(ListDirectoryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Идентификатор директории для индексов CloudFileEntry: для корня — Guid.Empty.
        Guid fileDirectoryId;
        if (request.DirectoryId.HasValue)
        {
            var dir = await _storage.GetDirectoryAsNoTracking(request.DirectoryId.Value, cancellationToken);
            if (dir is null)
                throw new DirectoryNotFoundException();
            if (dir.OwnerId != ownerId)
                throw new CloudAccessDeniedException();

            fileDirectoryId = dir.Id;
        }
        else
        {
            fileDirectoryId = CloudHierarchyStorage.RootDirectoryId;
        }

        var subdirs = await _storage.ListSubdirectories(ownerId, request.DirectoryId, cancellationToken);
        var files = await _storage.ListFilesInDirectory(ownerId, fileDirectoryId, cancellationToken);

        var response = new DirectoryListing();
        foreach (var d in subdirs)
        {
            response.Subdirs.Add(new DirectoryInfo
            {
                Id = d.Id.ToString(),
                ParentId = d.ParentId?.ToString() ?? string.Empty,
                Name = d.Name,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc)),
                UpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(d.UpdatedAt, DateTimeKind.Utc))
            });
        }
        foreach (var f in files)
        {
            response.Files.Add(new FileEntryInfo
            {
                Id = f.Id.ToString(),
                DirectoryId = f.DirectoryId == CloudHierarchyStorage.RootDirectoryId ? string.Empty : f.DirectoryId.ToString(),
                FileId = f.FileId.ToString(),
                Name = f.Name,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(f.CreatedAt, DateTimeKind.Utc))
            });
        }

        return response;
    }
}
