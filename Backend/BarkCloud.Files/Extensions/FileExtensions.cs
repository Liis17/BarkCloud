using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Extensions;

public static class FileExtensions
{
    /// <summary>
    /// Определяет MIME-тип содержимого по имени файла
    /// </summary>
    /// <param name="fileName">Имя файла</param>
    /// <returns>MIME-тип содержимого</returns>
    public static string GetContentType(this string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return "application/octet-stream";

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".heic" or ".heif" => "image/heic",
            ".tiff" or ".tif" => "image/tiff",

            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",

            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",

            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".opus" => "audio/opus",
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",

            ".zip" => "application/zip",
            ".rar" => "application/x-rar-compressed",
            ".7z" => "application/x-7z-compressed",

            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Определяет категорию медиа (<see cref="MediaKind"/>) по имени файла.
    /// Базируется на <see cref="GetContentType"/>: image/* → Photo, video/* → Video,
    /// audio/* → Audio, документы (pdf/office/text) → Document, иначе Other.
    /// </summary>
    public static MediaKind GetMediaKind(this string fileName)
    {
        var contentType = fileName.GetContentType();

        if (contentType.StartsWith("image/"))
            return MediaKind.Photo;
        if (contentType.StartsWith("video/"))
            return MediaKind.Video;
        if (contentType.StartsWith("audio/"))
            return MediaKind.Audio;

        return contentType switch
        {
            "application/pdf"
            or "application/msword"
            or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            or "application/vnd.ms-excel"
            or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            or "application/vnd.ms-powerpoint"
            or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => MediaKind.Document,
            _ when contentType.StartsWith("text/") => MediaKind.Document,
            _ => MediaKind.Other
        };
    }
}
