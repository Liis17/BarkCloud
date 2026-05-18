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
    private readonly CloudHierarchyStorage _storage;
    private readonly UploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListDirectoryDetailedCommandHandler> _logger;

    public ListDirectoryDetailedCommandHandler(
        CloudHierarchyStorage storage,
        UploadedFilesStorage uploadedFiles,
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

        var subdirs = await _storage.ListSubdirectories(ownerId, request.DirectoryId, cancellationToken);
        var entries = await _storage.ListFilesInDirectory(ownerId, fileDirectoryId, cancellationToken);

        var fileIds = entries.Select(e => e.FileId).Distinct().ToList();
        var files = fileIds.Count == 0
            ? new List<Domain.UploadFile>()
            : await _uploadedFiles.GetFiles(fileIds);
        var filesById = files.ToDictionary(f => f.Id);

        var previewsByOriginal = await _uploadedFiles.GetPreviewsForFiles(fileIds, cancellationToken);
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

        foreach (var e in entries)
        {
            var entryInfo = new FileEntryInfo
            {
                Id = e.Id.ToString(),
                DirectoryId = e.DirectoryId == CloudHierarchyStorage.RootDirectoryId ? string.Empty : e.DirectoryId.ToString(),
                FileId = e.FileId.ToString(),
                Name = e.Name,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc))
            };

            UploadFileInfo? fileInfo = null;
            if (filesById.TryGetValue(e.FileId, out var file))
            {
                previewsByOriginal.TryGetValue(file.Id, out var previews);
                fileInfo = file.ToGrpc(baseUrl, previews);
            }

            response.Files.Add(new FileEntryDetailed
            {
                Entry = entryInfo,
                File = fileInfo ?? new UploadFileInfo { Id = e.FileId.ToString() }
            });
        }

        _logger.LogDebug(
            "ListDirectoryDetailed: owner={Owner} dir={Dir} subdirs={Subdirs} files={Files}",
            ownerId, fileDirectoryId, subdirs.Count, entries.Count);

        return response;
    }
}
