using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListTrash;

/// <summary>
/// Листинг файлов в корзине владельца (от свежеудалённых к старым) с cursor-пагинацией,
/// датами удаления/окончательной зачистки и полной информацией о файле (превью/URL).
/// </summary>
public class ListTrashCommandHandler : IRequestHandler<ListTrashCommand, ListTrashResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly ICloudHierarchyStorage _storage;
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListTrashCommandHandler> _logger;

    public ListTrashCommandHandler(
        ICloudHierarchyStorage storage,
        IUploadedFilesStorage uploadedFiles,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListTrashCommandHandler> logger)
    {
        _storage = storage;
        _uploadedFiles = uploadedFiles;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListTrashResponse> Handle(ListTrashCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var page = await _storage.ListTrashedPage(ownerId, request.CursorDeletedAt, request.CursorEntryId, limit, cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListTrashResponse();
        if (page.Count == 0)
            return response;

        var fileIds = page.Select(e => e.FileId).Distinct().ToList();
        var filesById = (await _uploadedFiles.GetFiles(fileIds)).ToDictionary(f => f.Id);
        var previewsByFile = await _uploadedFiles.GetPreviewsForFiles(fileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        foreach (var e in page)
        {
            var entryInfo = new FileEntryInfo
            {
                Id = e.Id.ToString(),
                DirectoryId = e.DirectoryId == CloudHierarchyStorage.RootDirectoryId ? string.Empty : e.DirectoryId.ToString(),
                FileId = e.FileId.ToString(),
                Name = e.Name,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc))
            };

            UploadFileInfo fileInfo;
            if (filesById.TryGetValue(e.FileId, out var file))
            {
                previewsByFile.TryGetValue(file.Id, out var previews);
                fileInfo = file.ToGrpc(baseUrl, previews);
            }
            else
            {
                fileInfo = new UploadFileInfo { Id = e.FileId.ToString() };
            }

            var trashEntry = new TrashEntry
            {
                Entry = entryInfo,
                File = fileInfo
            };
            if (e.DeletedAt.HasValue)
                trashEntry.DeletedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.DeletedAt.Value, DateTimeKind.Utc));
            if (e.PurgeAt.HasValue)
                trashEntry.PurgeAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.PurgeAt.Value, DateTimeKind.Utc));

            response.Items.Add(trashEntry);
        }

        if (hasMore)
        {
            var last = page[^1];
            if (last.DeletedAt.HasValue)
                response.NextCursorDeletedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.DeletedAt.Value, DateTimeKind.Utc));
            response.NextCursorEntryId = last.Id.ToString();
        }

        _logger.LogDebug("ListTrash: owner={Owner} returned={Count} hasMore={HasMore}", ownerId, page.Count, hasMore);

        return response;
    }
}
