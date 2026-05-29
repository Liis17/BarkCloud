using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Album.ListAlbumItems;

public class ListAlbumItemsCommandHandler : IRequestHandler<ListAlbumItemsCommand, ListAlbumItemsResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IAlbumStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ICloudHierarchyStorage _hierarchyStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListAlbumItemsCommandHandler> _logger;

    public ListAlbumItemsCommandHandler(
        IAlbumStorage storage,
        IUploadedFilesStorage filesStorage,
        ICloudHierarchyStorage hierarchyStorage,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListAlbumItemsCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _hierarchyStorage = hierarchyStorage;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListAlbumItemsResponse> Handle(ListAlbumItemsCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var album = await _storage.GetAlbum(request.AlbumId, cancellationToken);
        if (album is null)
            throw new AlbumNotFoundException();
        if (album.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        var page = await _storage.ListItemsPage(album.Id, request.CursorAddedAt, request.CursorFileId, limit, cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListAlbumItemsResponse();
        if (page.Count == 0)
            return response;

        var fileIds = page.Select(x => x.FileId).Distinct().ToList();
        var files = (await _filesStorage.GetFiles(fileIds)).ToDictionary(f => f.Id);
        var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);
        // Файлы, находящиеся в корзине, не показываем в альбоме.
        var trashedFileIds = await _hierarchyStorage.GetEffectivelyTrashedFileIds(ownerId, fileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        foreach (var item in page)
        {
            // Пропускаем осиротевшие ссылки (файл удалён из облака).
            if (!files.TryGetValue(item.FileId, out var file))
                continue;

            // Пропускаем файлы в корзине.
            if (trashedFileIds.Contains(item.FileId))
                continue;

            // Опциональный фильтр по типу медиа.
            if (request.KindFilter.HasValue && file.MediaKind != request.KindFilter.Value)
                continue;

            previewsByFile.TryGetValue(file.Id, out var previews);
            response.Items.Add(new AlbumItemEntry
            {
                File = file.ToGrpc(baseUrl, previews),
                AddedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(item.AddedAt, DateTimeKind.Utc))
            });
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorAddedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.AddedAt, DateTimeKind.Utc));
            response.NextCursorFileId = last.FileId.ToString();
        }

        _logger.LogDebug("ListAlbumItems: album={Album} returned={Count} hasMore={HasMore}", album.Id, response.Items.Count, hasMore);

        return response;
    }
}
