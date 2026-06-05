using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ResolveAlbumShare;

/// <summary>
/// Анонимный резолв публичного альбома по токену. Возвращает элементы альбома (фото/видео) с
/// публичными temp-URL скачивания и URL превью, с cursor-пагинацией. «Эффективно удалённые»
/// (все записи каталога в корзине) файлы исключаются — как в обычном листинге альбома.
/// </summary>
public class ResolveAlbumShareCommandHandler : IRequestHandler<ResolveAlbumShareCommand, ResolveAlbumShareResponse>
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 200;

    private readonly IAlbumShareStorage _albumShares;
    private readonly IAlbumStorage _albums;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly ITempFilesStorage _tempFilesStorage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResolveAlbumShareCommandHandler> _logger;

    public ResolveAlbumShareCommandHandler(
        IAlbumShareStorage albumShares,
        IAlbumStorage albums,
        IUploadedFilesStorage filesStorage,
        ICloudHierarchyStorage hierarchy,
        ITempFilesStorage tempFilesStorage,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ResolveAlbumShareCommandHandler> logger)
    {
        _albumShares = albumShares;
        _albums = albums;
        _filesStorage = filesStorage;
        _hierarchy = hierarchy;
        _tempFilesStorage = tempFilesStorage;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ResolveAlbumShareResponse> Handle(ResolveAlbumShareCommand request, CancellationToken cancellationToken)
    {
        var share = await _albumShares.GetByToken(request.Token, cancellationToken);
        if (share is null)
            return new ResolveAlbumShareResponse { Found = false };

        var album = await _albums.GetAlbum(share.AlbumId, cancellationToken);
        if (album is null)
            return new ResolveAlbumShareResponse { Found = false };

        var ownerId = share.OwnerId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        // Открытие первой страницы публичного альбома считаем переходом.
        if (request.CursorAddedAt is null || request.CursorFileId is null)
            await _albumShares.IncrementClicks(share.Id, cancellationToken);

        var items = await _albums.ListItemsPage(
            share.AlbumId, request.CursorAddedAt, request.CursorFileId, limit, cancellationToken);

        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToList() : items;

        var response = new ResolveAlbumShareResponse
        {
            Found = true,
            AlbumName = album.Name,
            Description = album.Description ?? string.Empty
        };

        if (page.Count > 0)
        {
            var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
            var fileIds = page.Select(i => i.FileId).Distinct().ToList();
            var files = (await _filesStorage.GetFiles(fileIds)).ToDictionary(f => f.Id);
            var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);
            var trashed = await _hierarchy.GetEffectivelyTrashedFileIds(ownerId, fileIds, cancellationToken);

            var tempFiles = await _tempFilesStorage.CreateTempFilesBatchAsync(fileIds, cancellationToken);
            var tempByOriginal = tempFiles
                .GroupBy(t => t.OriginalFileId)
                .ToDictionary(g => g.Key, g => g.First().Id);

            foreach (var item in page)
            {
                if (!files.TryGetValue(item.FileId, out var file))
                    continue;
                if (trashed.Contains(item.FileId))
                    continue;

                var downloadUrl = tempByOriginal.TryGetValue(item.FileId, out var tempId)
                    ? FileUrlHelper.GenerateDownloadUrl(baseUrl, tempId)
                    : string.Empty;

                var previewUrl = string.Empty;
                if (previewsByFile.TryGetValue(item.FileId, out var previews) && previews.Count > 0)
                    previewUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, previews.OrderByDescending(p => p.TargetWidth).First().PreviewFileId);

                response.Items.Add(new PublicFileEntry
                {
                    FileId = item.FileId.ToString(),
                    Name = file.Filename ?? string.Empty,
                    MediaKind = (MediaKind)(int)file.MediaKind,
                    DownloadUrl = downloadUrl,
                    PreviewUrl = previewUrl,
                    FileSize = file.Size,
                    ImageWidth = file.ImageWidth ?? 0,
                    ImageHeight = file.ImageHeight ?? 0
                });
            }
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorAddedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.AddedAt, DateTimeKind.Utc));
            response.NextCursorFileId = last.FileId.ToString();
        }

        _logger.LogInformation("Резолв публичного альбома {ShareId} (album {AlbumId}): элементов {Count}",
            share.Id, share.AlbumId, response.Items.Count);

        return response;
    }
}
