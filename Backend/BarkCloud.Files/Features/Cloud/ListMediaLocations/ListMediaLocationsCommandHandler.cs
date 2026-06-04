using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMediaLocations;

/// <summary>
/// Точки для карты: фото/видео с GPS-координатами (<c>FileMetadata.Latitude/Longitude</c>),
/// cursor-пагинация от новых к старым. Клиент сам кластеризует точки по зуму.
/// На точку отдаём узкое превью (самое маленькое из доступных) для миниатюры в попапе.
/// </summary>
public class ListMediaLocationsCommandHandler : IRequestHandler<ListMediaLocationsCommand, ListMediaLocationsResponse>
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 1000;

    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListMediaLocationsCommandHandler> _logger;

    public ListMediaLocationsCommandHandler(
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListMediaLocationsCommandHandler> logger)
    {
        _filesStorage = filesStorage;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListMediaLocationsResponse> Handle(ListMediaLocationsCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var page = await _filesStorage.ListMediaWithLocationPage(
            ownerId, request.CursorCreatedAt, request.CursorFileId, limit, cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListMediaLocationsResponse();
        if (page.Count == 0)
            return response;

        var fileIds = page.Select(x => x.File.Id).ToList();
        var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        foreach (var item in page)
        {
            var point = new MediaLocationPoint
            {
                FileId = item.File.Id.ToString(),
                Latitude = item.Latitude,
                Longitude = item.Longitude,
                MediaKind = (BarkCloud.Proto.Files.MediaKind)(int)item.File.MediaKind,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(item.File.CreatedAt, DateTimeKind.Utc))
            };

            if (item.TakenAt.HasValue)
                point.TakenAt = Timestamp.FromDateTime(DateTime.SpecifyKind(item.TakenAt.Value, DateTimeKind.Utc));

            if (previewsByFile.TryGetValue(item.File.Id, out var previews) && previews.Count > 0)
            {
                var smallest = previews.OrderBy(p => p.TargetWidth).First();
                point.PreviewUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, smallest.PreviewFileId);
            }

            response.Points.Add(point);
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.File.CreatedAt, DateTimeKind.Utc));
            response.NextCursorFileId = last.File.Id.ToString();
        }

        _logger.LogDebug("ListMediaLocations: owner={Owner} returned={Count} hasMore={HasMore}", ownerId, response.Points.Count, hasMore);

        return response;
    }
}
