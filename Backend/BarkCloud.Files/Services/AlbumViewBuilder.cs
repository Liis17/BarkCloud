using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

namespace BarkCloud.Files.Services;

/// <summary>
/// Собирает <see cref="AlbumInfo"/> для альбомов: подставляет количество элементов и URL
/// превью обложки. Работает батчем, чтобы списки альбомов не делали N запросов.
/// </summary>
public class AlbumViewBuilder
{
    private const int PreferredCoverWidth = 512;

    private readonly AlbumStorage _albumStorage;
    private readonly UploadedFilesStorage _filesStorage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public AlbumViewBuilder(
        AlbumStorage albumStorage,
        UploadedFilesStorage filesStorage,
        RunSettings runSettings,
        IConfiguration configuration)
    {
        _albumStorage = albumStorage;
        _filesStorage = filesStorage;
        _runSettings = runSettings;
        _configuration = configuration;
    }

    public async Task<List<AlbumInfo>> BuildAsync(IReadOnlyList<Album> albums, CancellationToken cancellationToken)
    {
        var result = new List<AlbumInfo>(albums.Count);
        if (albums.Count == 0)
            return result;

        var albumIds = albums.Select(a => a.Id).ToList();
        var counts = await _albumStorage.GetItemCounts(albumIds, cancellationToken);

        var coverFileIds = albums
            .Where(a => a.CoverFileId.HasValue)
            .Select(a => a.CoverFileId!.Value)
            .Distinct()
            .ToList();

        var previewsByCover = coverFileIds.Count > 0
            ? await _filesStorage.GetPreviewsForFiles(coverFileIds, cancellationToken)
            : new Dictionary<Guid, List<FilePreview>>();

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        foreach (var album in albums)
        {
            counts.TryGetValue(album.Id, out var count);

            string? coverUrl = null;
            if (album.CoverFileId.HasValue
                && previewsByCover.TryGetValue(album.CoverFileId.Value, out var previews)
                && previews.Count > 0)
            {
                var chosen = previews.FirstOrDefault(p => p.TargetWidth == PreferredCoverWidth)
                             ?? previews[^1];
                coverUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, chosen.PreviewFileId);
            }

            result.Add(album.ToGrpc(count, coverUrl));
        }

        return result;
    }
}
