using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListFavorites;

public class ListFavoritesCommandHandler : IRequestHandler<ListFavoritesCommand, ListFavoritesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IFavoriteFilesStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ICloudHierarchyStorage _hierarchyStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListFavoritesCommandHandler> _logger;

    public ListFavoritesCommandHandler(
        IFavoriteFilesStorage storage,
        IUploadedFilesStorage filesStorage,
        ICloudHierarchyStorage hierarchyStorage,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListFavoritesCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _hierarchyStorage = hierarchyStorage;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListFavoritesResponse> Handle(ListFavoritesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var page = await _storage.ListPage(ownerId, request.CursorCreatedAt, request.CursorFileId, limit, cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListFavoritesResponse();
        if (page.Count == 0)
            return response;

        var fileIds = page.Select(x => x.FileId).Distinct().ToList();
        var files = (await _filesStorage.GetFiles(fileIds)).ToDictionary(f => f.Id);
        var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);
        // Файлы, находящиеся в корзине, в избранном не показываем.
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

            previewsByFile.TryGetValue(file.Id, out var previews);
            response.Items.Add(new FavoriteEntry
            {
                File = file.ToGrpc(baseUrl, previews),
                FavoritedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc))
            });
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorFavoritedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorFileId = last.FileId.ToString();
        }

        _logger.LogDebug("ListFavorites: owner={Owner} returned={Count} hasMore={HasMore}", ownerId, response.Items.Count, hasMore);

        return response;
    }
}
