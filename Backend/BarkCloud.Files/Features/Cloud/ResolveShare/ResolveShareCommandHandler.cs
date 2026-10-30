using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ResolveShare;

public class ResolveShareCommandHandler : IRequestHandler<ResolveShareCommand, ResolveShareResponse>
{
    private readonly IShareStorage _storage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ITempFilesStorage _tempFilesStorage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResolveShareCommandHandler> _logger;

    public ResolveShareCommandHandler(
        IShareStorage storage,
        IUploadedFilesStorage filesStorage,
        ITempFilesStorage tempFilesStorage,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ResolveShareCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _tempFilesStorage = tempFilesStorage;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ResolveShareResponse> Handle(ResolveShareCommand request, CancellationToken cancellationToken)
    {
        var share = await _storage.GetByToken(request.Token, cancellationToken);
        if (share is null)
            return new ResolveShareResponse { Found = false };

        // Файл мог быть удалён владельцем после создания ссылки — отдадим 404 вместо мёртвой temp-ссылки.
        var file = await _filesStorage.GetFile(share.FileId);
        if (file is null || !file.IsReady())
        {
            _logger.LogWarning(
                "Резолв публичной ссылки {ShareId}: файл {FileId} отсутствует или ещё не готов",
                share.Id,
                share.FileId);
            return new ResolveShareResponse { Found = false };
        }

        await _storage.IncrementClicks(share.Id, cancellationToken);

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
        var links = await VideoQualityLinkBuilder.CreateAsync(
            _filesStorage, _tempFilesStorage, [file], baseUrl, cancellationToken);
        var fileLinks = links[share.FileId];
        var downloadUrl = fileLinks.OriginalUrl;

        // Публичное превью (фото/видео) для страницы просмотра: берём самое крупное доступное.
        // Превью-блобы отдаются анонимно через /download/{previewFileId}.
        var previews = await _filesStorage.GetPreviewsForFile(share.FileId, cancellationToken);
        var bestPreview = previews.Count > 0 ? previews.OrderByDescending(p => p.TargetWidth).First() : null;
        var previewUrl = bestPreview is null ? string.Empty : FileUrlHelper.GenerateDownloadUrl(baseUrl, bestPreview.PreviewFileId);

        _logger.LogInformation("Резолв публичной ссылки {ShareId} → файл {FileId}", share.Id, share.FileId);

        var response = new ResolveShareResponse
        {
            Found = true,
            FileId = share.FileId.ToString(),
            Name = share.Name,
            DownloadUrl = downloadUrl,
            MediaKind = (BarkCloud.Proto.Files.MediaKind)(int)file.MediaKind,
            PreviewUrl = previewUrl,
            ImageWidth = file.ImageWidth ?? 0,
            ImageHeight = file.ImageHeight ?? 0,
            FileSize = file.Size
        };
        response.VideoQualities.AddRange(fileLinks.VideoQualities);
        return response;
    }
}
