namespace BarkCloud.Files.Helpers;

/// <summary>
/// Определение HDR по характеристике передачи (color_transfer) из ffprobe.
/// Надёжный признак: smpte2084 = PQ (HDR10/HDR10+/Dolby Vision), arib-std-b67 = HLG.
/// </summary>
public static class VideoHdr
{
    public static bool IsHdr(string? colorTransfer)
    {
        var t = colorTransfer?.Trim().ToLowerInvariant();
        return t is "smpte2084" or "arib-std-b67";
    }
}
