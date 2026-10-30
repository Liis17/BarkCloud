using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;

using VideoQualityInfo = BarkCloud.Proto.Files.VideoQualityInfo;

namespace BarkCloud.Files.Services;

internal static class VideoQualityLinkBuilder
{
    public static async Task<Dictionary<Guid, VideoDownloadLinks>> CreateAsync(
        IUploadedFilesStorage filesStorage,
        ITempFilesStorage tempFilesStorage,
        IEnumerable<UploadFile> sourceFiles,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var files = sourceFiles.DistinctBy(file => file.Id).ToList();
        if (files.Count == 0)
            return [];

        var videoFiles = files
            .Where(file => file.Type == UploadFileType.CloudFile && file.MediaKind == MediaKind.Video)
            .ToList();
        var variantsByOriginal = videoFiles.Count == 0
            ? new Dictionary<Guid, List<VideoVariantDownloadInfo>>()
            : await filesStorage.GetVideoVariantsForFiles(videoFiles.Select(file => file.Id), cancellationToken);
        var allFileIds = files.Select(file => file.Id)
            .Concat(variantsByOriginal.Values.SelectMany(variants => variants).Select(variant => variant.FileId))
            .Distinct()
            .ToList();
        var tempFiles = await tempFilesStorage.CreateTempFilesBatchAsync(allFileIds, cancellationToken);
        var urlsByFileId = tempFiles.ToDictionary(
            tempFile => tempFile.OriginalFileId,
            tempFile => FileUrlHelper.GenerateDownloadUrl(baseUrl, tempFile.Id));

        var result = new Dictionary<Guid, VideoDownloadLinks>(files.Count);
        foreach (var file in files)
        {
            var originalUrl = urlsByFileId.GetValueOrDefault(file.Id, string.Empty);
            var qualities = new List<VideoQualityInfo>();
            if (file.Type == UploadFileType.CloudFile
                && file.MediaKind == MediaKind.Video
                && !string.IsNullOrEmpty(originalUrl))
            {
                qualities.Add(new VideoQualityInfo
                {
                    Height = file.ImageHeight ?? 0,
                    Width = file.ImageWidth ?? 0,
                    FileSize = file.Size,
                    IsOriginal = true,
                    Url = originalUrl
                });

                if (variantsByOriginal.TryGetValue(file.Id, out var variants))
                {
                    foreach (var variant in variants)
                    {
                        if (!urlsByFileId.TryGetValue(variant.FileId, out var variantUrl))
                            continue;

                        qualities.Add(new VideoQualityInfo
                        {
                            Height = variant.Height,
                            Width = variant.Width,
                            FileSize = variant.FileSize,
                            IsOriginal = false,
                            Url = variantUrl
                        });
                    }
                }
            }

            result[file.Id] = new VideoDownloadLinks(originalUrl, qualities);
        }

        return result;
    }
}

internal sealed record VideoDownloadLinks(string OriginalUrl, List<VideoQualityInfo> VideoQualities);
