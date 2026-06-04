using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using UploadFileType = BarkCloud.Proto.Files.UploadFileType;

namespace BarkCloud.Files.Mapping;

public static class UploadFileMapping
{
    /// <summary>
    /// Мапит UploadFile в gRPC-DTO. Если переданы превью — заполняет соответствующий repeated.
    /// PreviewUrl (deprecated single-preview) для совместимости отдаём как ссылку на самое узкое доступное превью.
    /// </summary>
    public static UploadFileInfo ToGrpc(
        this UploadFile file,
        string? publicBaseUrl = null,
        IReadOnlyList<FilePreview>? previews = null)
    {
        var info = new UploadFileInfo
        {
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(file.CreatedAt, DateTimeKind.Utc)),
            Etag = file.Etag ?? string.Empty,
            FileName = file.Filename ?? string.Empty,
            Id = file.Id.ToString(),
            Type = (UploadFileType)(int)file.Type,
            UploadedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(file.UploadedAt ?? DateTime.MinValue, DateTimeKind.Utc)),
            FileSize = file.Size,
            FileUrl = publicBaseUrl is null
                ? string.Empty
                : FileUrlHelper.GenerateDownloadUrl(publicBaseUrl, file.Id),
            ImageWidth = file.ImageWidth ?? 0,
            ImageHeight = file.ImageHeight ?? 0,
            MediaKind = (BarkCloud.Proto.Files.MediaKind)(int)file.MediaKind,
            UploadDeviceName = file.UploadDeviceName ?? string.Empty
        };

        info.Uploaders.AddRange(file.Uploaders);

        // TargetWidth == 0 — служебная связка JpegView (полноразмерный вид), не обычное превью.
        var realPreviews = previews?.Where(x => x.TargetWidth > 0).OrderBy(x => x.TargetWidth).ToList();
        if (realPreviews is { Count: > 0 })
        {
            foreach (var p in realPreviews)
            {
                info.Previews.Add(new FilePreviewInfo
                {
                    PreviewFileId = p.PreviewFileId.ToString(),
                    TargetWidth = p.TargetWidth,
                    ActualWidth = p.ActualWidth,
                    ActualHeight = p.ActualHeight,
                    PreviewUrl = publicBaseUrl is null
                        ? string.Empty
                        : FileUrlHelper.GenerateDownloadUrl(publicBaseUrl, p.PreviewFileId)
                });
            }

            // Для legacy-клиентов отдаём ссылку на самое узкое превью.
            var smallest = realPreviews.First();
            info.PreviewUrl = publicBaseUrl is null
                ? string.Empty
                : FileUrlHelper.GenerateDownloadUrl(publicBaseUrl, smallest.PreviewFileId);
        }

        // JpegView: полноразмерный JPEG для просмотра. Для оригинала-JPEG указывает на сам файл.
        if (file.JpegViewFileId.HasValue)
        {
            info.JpegViewFileId = file.JpegViewFileId.Value.ToString();
            info.JpegViewUrl = publicBaseUrl is null
                ? string.Empty
                : FileUrlHelper.GenerateDownloadUrl(publicBaseUrl, file.JpegViewFileId.Value);
        }

        return info;
    }
}
