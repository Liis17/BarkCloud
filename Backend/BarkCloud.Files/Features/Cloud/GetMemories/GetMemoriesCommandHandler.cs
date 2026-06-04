using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.GetMemories;

/// <summary>
/// «Воспоминания — В этот день»: фото/видео, снятые сегодняшнего числа в прошлые годы
/// (по <c>FileMetadata.TakenAt</c>), сгруппированные по году от свежего к старому.
/// Группировка делается в памяти из ограниченной выборки — на масштабе личного облака дёшево.
/// </summary>
public class GetMemoriesCommandHandler : IRequestHandler<GetMemoriesCommand, GetMemoriesResponse>
{
    private const int DefaultPerYear = 12;
    private const int MaxPerYear = 60;
    private const int MaxTotalScan = 500;

    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GetMemoriesCommandHandler> _logger;

    public GetMemoriesCommandHandler(
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<GetMemoriesCommandHandler> logger)
    {
        _filesStorage = filesStorage;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GetMemoriesResponse> Handle(GetMemoriesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var now = DateTime.UtcNow;

        var month = request.Month is >= 1 and <= 12 ? request.Month : now.Month;
        var day = request.Day is >= 1 and <= 31 ? request.Day : now.Day;
        var perYear = request.PerYearLimit <= 0 ? DefaultPerYear : Math.Min(request.PerYearLimit, MaxPerYear);

        var matches = await _filesStorage.ListMemoriesForDay(ownerId, month, day, MaxTotalScan, cancellationToken);

        var response = new GetMemoriesResponse();
        if (matches.Count == 0)
            return response;

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        // Превью грузим одним батчем для отдаваемых файлов (после обрезки per-year).
        var byYear = matches
            .GroupBy(x => x.TakenAt.Year)
            .OrderByDescending(g => g.Key);

        var groups = new List<(int Year, int Total, List<MemoryMediaItem> Items)>();
        foreach (var g in byYear)
            groups.Add((g.Key, g.Count(), g.Take(perYear).ToList()));

        var pageFileIds = groups.SelectMany(g => g.Items.Select(i => i.File.Id)).ToList();
        var previewsByFile = await _filesStorage.GetPreviewsForFiles(pageFileIds, cancellationToken);

        foreach (var (year, total, items) in groups)
        {
            var group = new MemoryGroup
            {
                Year = year,
                YearsAgo = now.Year - year,
                TotalCount = total
            };

            foreach (var item in items)
            {
                previewsByFile.TryGetValue(item.File.Id, out var previews);
                group.Items.Add(item.File.ToGrpc(baseUrl, previews));
            }

            response.Groups.Add(group);
        }

        _logger.LogDebug("GetMemories: owner={Owner} {Month}-{Day} groups={Groups}", ownerId, month, day, response.Groups.Count);

        return response;
    }
}
