using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;
using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.ListDirectoryDetailed;

public class ListDirectoryDetailedCommandHandler : IRequestHandler<ListDirectoryDetailedCommand, DirectoryListingDetailed>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly ICloudHierarchyStorage _storage;
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListDirectoryDetailedCommandHandler> _logger;

    public ListDirectoryDetailedCommandHandler(
        ICloudHierarchyStorage storage,
        IUploadedFilesStorage uploadedFiles,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListDirectoryDetailedCommandHandler> logger)
    {
        _storage = storage;
        _uploadedFiles = uploadedFiles;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DirectoryListingDetailed> Handle(ListDirectoryDetailedCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

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
        var entries = await _storage.ListFilesInDirectoryPage(
            ownerId, fileDirectoryId, request.CursorName, request.CursorEntryId, limit, cancellationToken);
        var hasMore = entries.Count > limit;
        var page = hasMore ? entries.Take(limit).ToList() : entries;
        var subdirs = await _storage.ListSubdirectories(ownerId, request.DirectoryId, cancellationToken);

        var fileIds = page.Select(e => e.FileId).Distinct().ToList();
        var files = fileIds.Count == 0
            ? new List<Domain.UploadFile>()
            : await _uploadedFiles.GetFiles(fileIds);
        var filesById = files.ToDictionary(f => f.Id);

        var previewsByOriginal = await _uploadedFiles.GetPreviewsForFiles(filesById.Keys, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        var response = new DirectoryListingDetailed();
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

        foreach (var e in page)
        {
            if (!filesById.TryGetValue(e.FileId, out var file))
                continue;

            var entryInfo = new FileEntryInfo
            {
                Id = e.Id.ToString(),
                DirectoryId = e.DirectoryId == CloudHierarchyStorage.RootDirectoryId ? string.Empty : e.DirectoryId.ToString(),
                FileId = e.FileId.ToString(),
                Name = e.Name,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc))
            };

            previewsByOriginal.TryGetValue(file.Id, out var previews);

            response.Files.Add(new FileEntryDetailed
            {
                Entry = entryInfo,
                File = file.ToGrpc(baseUrl, previews)
            });
        }

        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            response.NextCursorName = last.Name;
            response.NextCursorEntryId = last.Id.ToString();
        }

        _logger.LogDebug(
            "ListDirectoryDetailed: owner={Owner} dir={Dir} subdirs={Subdirs} files={Files}",
            ownerId, fileDirectoryId, subdirs.Count, response.Files.Count);

        return response;
    }
}
