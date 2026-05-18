using BarkCloud.Files.Domain;

namespace BarkCloud.Files.Helpers;

public static class ImageDetection
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".bmp", ".tiff", ".tif"
    };

    public static bool IsImageExtension(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return false;

        var ext = Path.GetExtension(filename);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    public static bool IsImage(UploadFile file)
    {
        if (file.ImageWidth is > 0)
            return true;

        return IsImageExtension(file.Filename);
    }

    public static IReadOnlyCollection<string> Extensions => ImageExtensions;
}
