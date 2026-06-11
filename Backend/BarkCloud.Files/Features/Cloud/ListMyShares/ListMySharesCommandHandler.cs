using BarkCloud.Files.Features.Cloud.CreateShare;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyShares;

public class ListMySharesCommandHandler : IRequestHandler<ListMySharesCommand, ListMySharesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IShareStorage _storage;
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListMySharesCommandHandler> _logger;

    public ListMySharesCommandHandler(
        IShareStorage storage,
        IUploadedFilesStorage uploadedFiles,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListMySharesCommandHandler> logger)
    {
        _storage = storage;
        _uploadedFiles = uploadedFiles;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListMySharesResponse> Handle(ListMySharesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var page = await _storage.ListPage(ownerId, request.CursorCreatedAt, request.CursorShareId, limit, cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListMySharesResponse();

        // Батчем подтягиваем тип медиа и превью файлов (как SearchFilesCommandHandler).
        var fileIds = page.Select(s => s.FileId).Distinct().ToList();
        var filesById = (await _uploadedFiles.GetFiles(fileIds)).ToDictionary(f => f.Id);
        var previewsByOriginal = await _uploadedFiles.GetPreviewsForFiles(fileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        foreach (var share in page)
        {
            var info = CreateShareCommandHandler.ToGrpc(share);
            if (filesById.TryGetValue(share.FileId, out var file))
                info.MediaKind = (MediaKind)(int)file.MediaKind;
            if (previewsByOriginal.TryGetValue(share.FileId, out var previews))
            {
                var smallest = previews.Where(p => p.TargetWidth > 0).OrderBy(p => p.TargetWidth).FirstOrDefault();
                if (smallest is not null)
                    info.PreviewUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, smallest.PreviewFileId);
            }
            response.Shares.Add(info);
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorShareId = last.Id.ToString();
        }

        _logger.LogDebug("ListMyShares: owner={Owner} returned={Count} hasMore={HasMore}", ownerId, response.Shares.Count, hasMore);

        return response;
    }
}
