namespace BarkCloud.Transcoder;

public static class FfmpegCommand
{
    private static readonly Dictionary<int, int> CrfByHeight = new()
    {
        [360] = 36,
        [720] = 34,
        [1080] = 32,
        [1440] = 30,
        [2160] = 28
    };

    public static IReadOnlyList<int> SupportedHeights { get; } = CrfByHeight.Keys.Order().ToArray();

    public static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int height, int threads)
    {
        if (!CrfByHeight.TryGetValue(height, out var crf))
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Высота не входит в поддерживаемую лестницу.");
        }

        if (threads is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(threads), "Число потоков должно быть от 1 до 64.");
        }

        var svtParallelismLevel = Math.Clamp((int)Math.Ceiling(Math.Log2(threads)), 1, 6);
        var scale = $"scale=w='if(gte(iw,ih),-2,{height})':h='if(gte(iw,ih),{height},-2)':flags=lanczos";

        return
        [
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-y",
            "-i", inputPath,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-vf", scale,
            "-c:v", "libsvtav1",
            "-pix_fmt", "yuv420p10le",
            "-preset", "8",
            "-crf", crf.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-threads", threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-svtav1-params", $"lp={svtParallelismLevel}",
            "-c:a", "libopus",
            "-b:a", "128k",
            "-progress", "pipe:1",
            "-nostats",
            "-f", "webm",
            outputPath
        ];
    }
}
