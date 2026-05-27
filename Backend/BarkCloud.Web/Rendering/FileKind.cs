namespace BarkCloud.Web.Rendering;

/// <summary>Сопоставление расширения файла визуальному типу (см. .file-icon в ClientApp/src/styles/pages.css).</summary>
public static class FileKind
{
    public static (string Kind, string Ext) Classify(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var ext = dot >= 0 && dot < fileName.Length - 1
            ? fileName[(dot + 1)..].ToUpperInvariant()
            : "FILE";

        var kind = ext.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "heic" or "bmp" or "tiff" or "arw" or "raw" or "dng" => "img",
            "mp4" or "mov" or "mkv" or "avi" or "webm" or "m4v" => "vid",
            "pdf" => "pdf",
            "doc" or "docx" or "txt" or "md" or "rtf" or "odt" or "xls" or "xlsx" or "csv" or "ppt" or "pptx" => "doc",
            "zip" or "rar" or "7z" or "tar" or "gz" => "zip",
            "js" or "jsx" or "ts" or "tsx" or "cs" or "py" or "java" or "go" or "rs" or "html" or "css" or "json" or "xml" => "code",
            "mp3" or "flac" or "wav" or "ogg" or "aac" or "m4a" => "audio",
            _ => "doc"
        };

        return (kind, ext);
    }

    public static bool IsVideo(string fileName) => Classify(fileName).Kind == "vid";
}
