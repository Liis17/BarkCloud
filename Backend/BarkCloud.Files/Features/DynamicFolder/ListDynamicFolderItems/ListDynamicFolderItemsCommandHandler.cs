using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.DynamicFolder.ListDynamicFolderItems;

public class ListDynamicFolderItemsCommandHandler : IRequestHandler<ListDynamicFolderItemsCommand, ListDynamicFolderItemsResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxEntryNames = 5;

    private readonly IDynamicFolderStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ICloudHierarchyStorage _cloudHierarchy;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListDynamicFolderItemsCommandHandler> _logger;

    public ListDynamicFolderItemsCommandHandler(
        IDynamicFolderStorage storage,
        IUploadedFilesStorage filesStorage,
        ICloudHierarchyStorage cloudHierarchy,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListDynamicFolderItemsCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _cloudHierarchy = cloudHierarchy;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListDynamicFolderItemsResponse> Handle(ListDynamicFolderItemsCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var duplicateSystemFolder = SystemDynamicFolders.IsDuplicateKey(request.FolderId);
        var duplicateMediaOnly = SystemDynamicFolders.IsDuplicateMediaKey(request.FolderId);

        // Резолвим критерии: системная папка (по well-known ключу) или пользовательская (по Guid с проверкой владельца).
        DynamicFolderCriteria criteria;
        if (SystemDynamicFolders.IsSystemKey(request.FolderId))
        {
            criteria = SystemDynamicFolders.CriteriaFor(request.FolderId)
                       ?? throw new DynamicFolderNotFoundException();
        }
        else
        {
            if (!Guid.TryParse(request.FolderId, out var folderId))
                throw new DynamicFolderNotFoundException();
            var folder = await _storage.GetFolder(folderId, cancellationToken);
            if (folder is null)
                throw new DynamicFolderNotFoundException();
            if (folder.OwnerId != ownerId)
                throw new CloudAccessDeniedException();
            criteria = folder.Criteria;
        }

        var now = DateTime.UtcNow;
        var duplicatePage = duplicateSystemFolder
            ? await _storage.ListDuplicateItemsPage(ownerId, duplicateMediaOnly, request.CursorCreatedAt, request.CursorFileId, limit, cancellationToken)
            : null;
        var page = duplicatePage?.Select(x => x.File).ToList()
                   ?? await _storage.ListItemsPage(ownerId, criteria, now, request.CursorCreatedAt, request.CursorFileId, limit, cancellationToken);
        var duplicateGroupByFileId = duplicatePage?.ToDictionary(x => x.File.Id, x => x.GroupKey);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListDynamicFolderItemsResponse();
        if (page.Count == 0)
            return response;

        var fileIds = page.Select(f => f.Id).ToList();
        var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        // Записи каталога владельца — нужны фронту для переименования/удаления/«показать в папке».
        var entries = await _cloudHierarchy.GetLiveEntriesForFiles(ownerId, fileIds, cancellationToken);
        var entriesByFileId = entries
            .GroupBy(e => e.FileId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Count = g.Count(),
                    Names = g.OrderByDescending(x => x.CreatedAt).Take(MaxEntryNames).Select(x => x.Name).ToList(),
                    Ids = g.OrderByDescending(x => x.CreatedAt).Select(x => x.Id.ToString()).ToList()
                });

        foreach (var file in page)
        {
            // Опциональный фильтр по типу медиа (применяется после выборки, как в альбомах).
            if (request.KindFilter.HasValue && file.MediaKind != request.KindFilter.Value)
                continue;

            previewsByFile.TryGetValue(file.Id, out var previews);
            var item = new UserImageItem { File = file.ToGrpc(baseUrl, previews) };
            if (duplicateGroupByFileId is not null && duplicateGroupByFileId.TryGetValue(file.Id, out var groupKey))
                item.DuplicateGroupKey = groupKey;
            if (entriesByFileId.TryGetValue(file.Id, out var meta))
            {
                item.EntriesCount = meta.Count;
                item.EntryNames.AddRange(meta.Names);
                item.EntryIds.AddRange(meta.Ids);
            }

            response.Items.Add(item);
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorFileId = last.Id.ToString();
        }

        _logger.LogDebug("ListDynamicFolderItems: folder={Folder} owner={Owner} returned={Count} hasMore={HasMore}",
            request.FolderId, ownerId, response.Items.Count, hasMore);

        return response;
    }
}
