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
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly ICloudHierarchyStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<ListDirectoryCommandHandler> _logger;

    public ListDirectoryCommandHandler(
        ICloudHierarchyStorage storage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        ILogger<ListDirectoryCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
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

        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);
        var files = await _storage.ListFilesInDirectoryPage(
            ownerId, fileDirectoryId, request.CursorName, request.CursorEntryId, limit, cancellationToken);
        var hasMore = files.Count > limit;
        var page = hasMore ? files.Take(limit).ToList() : files;
        var subdirs = await _storage.ListSubdirectories(ownerId, request.DirectoryId, cancellationToken);
        var readyFileIds = page.Count == 0
            ? new HashSet<Guid>()
            : (await _filesStorage.GetFiles(page.Select(x => x.FileId).Distinct().ToList()))
                .Select(x => x.Id)
                .ToHashSet();

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
        foreach (var f in page.Where(x => readyFileIds.Contains(x.FileId)))
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

        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            response.NextCursorName = last.Name;
            response.NextCursorEntryId = last.Id.ToString();
        }

        return response;
    }
}
