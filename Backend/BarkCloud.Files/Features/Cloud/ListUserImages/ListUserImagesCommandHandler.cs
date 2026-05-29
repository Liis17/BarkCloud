using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListUserImages;

public class ListUserImagesCommandHandler : IRequestHandler<ListUserImagesCommand, ListUserImagesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxEntryNames = 5;

    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly ICloudHierarchyStorage _cloudHierarchy;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListUserImagesCommandHandler> _logger;

    public ListUserImagesCommandHandler(
        IUploadedFilesStorage uploadedFiles,
        ICloudHierarchyStorage cloudHierarchy,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListUserImagesCommandHandler> logger)
    {
        _uploadedFiles = uploadedFiles;
        _cloudHierarchy = cloudHierarchy;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListUserImagesResponse> Handle(ListUserImagesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var page = await _uploadedFiles.ListUserImagesPage(
            ownerId, request.CursorCreatedAt, request.CursorFileId, limit, cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListUserImagesResponse();
        if (page.Count == 0)
            return response;

        var pageFileIds = page.Select(f => f.Id).ToList();

        // Подгружаем превью для всех файлов страницы одним запросом.
        var previewsByOriginal = await _uploadedFiles.GetPreviewsForFiles(pageFileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        // Считаем количество и имена FileEntry для каждого FileId пачкой.
        // Делаем в памяти после быстрого выбора всех записей — это надёжнее, чем GroupBy+Take на стороне БД,
        // и стоимость низкая: список ограничен limit (≤200) копий каждого файла на одного владельца, что на практике небольшие десятки.
        var entries = await _cloudHierarchy.GetEntriesForFiles(ownerId, pageFileIds, cancellationToken);

        var entriesByFileId = entries
            .GroupBy(e => e.FileId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Count = g.Count(),
                    Names = g.OrderByDescending(x => x.CreatedAt)
                            .Take(MaxEntryNames)
                            .Select(x => x.Name)
                            .ToList()
                });

        foreach (var file in page)
        {
            previewsByOriginal.TryGetValue(file.Id, out var previews);
            var item = new UserImageItem
            {
                File = file.ToGrpc(baseUrl, previews)
            };

            if (entriesByFileId.TryGetValue(file.Id, out var meta))
            {
                item.EntriesCount = meta.Count;
                item.EntryNames.AddRange(meta.Names);
            }

            response.Items.Add(item);
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorFileId = last.Id.ToString();
        }

        _logger.LogDebug(
            "ListUserImages: owner={Owner} returned={Count} hasMore={HasMore}",
            ownerId, page.Count, hasMore);

        return response;
    }
}
