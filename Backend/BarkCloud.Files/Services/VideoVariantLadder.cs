namespace BarkCloud.Files.Services;

internal static class VideoVariantLadder
{
    public static readonly int[] Heights = [360, 720, 1080, 1440, 2160];

    public static int GetShortSide(int? width, int? height) =>
        width is > 0 && height is > 0 ? Math.Min(width.Value, height.Value) : 0;

    public static List<int> GetMissingHeights(
        int shortSide,
        IEnumerable<int> existingHeights,
        IEnumerable<int> skippedHeights)
    {
        var existing = existingHeights.ToHashSet();
        var skipped = skippedHeights.ToHashSet();
        return Heights
            .Where(height => height < shortSide && !existing.Contains(height) && !skipped.Contains(height))
            .ToList();
    }

    public static bool ShouldSkipBySize(long originalSize, long variantSize) => variantSize >= originalSize;
}

internal static class VideoVariantFilter
{
    public static string? GetSkipReason(
        long sourceSize,
        double? durationSeconds,
        int? maxSourceSizeMb,
        int? maxDurationMinutes)
    {
        if (maxSourceSizeMb is { } maxSizeMb && sourceSize > (long)maxSizeMb * 1024 * 1024)
            return $"Размер исходного видео превышает ограничение {maxSizeMb} МБ.";

        if (maxDurationMinutes is { } maxDuration
            && (durationSeconds is null || durationSeconds > (double)maxDuration * 60))
            return durationSeconds is null
                ? "Длительность видео неизвестна при включённом ограничении."
                : $"Длительность исходного видео превышает ограничение {maxDuration} мин.";

        return null;
    }
}
