using FFMpegCore;

namespace BarkCloud.Files.Services;

/// <summary>
/// Технические метаданные видеоконтейнера, собранные через ffprobe.
/// Любое поле может быть «пустым» (0 / null), если ffprobe не смог его определить.
/// </summary>
public record VideoProbe(
    int Width,
    int Height,
    TimeSpan Duration,
    string? VideoCodec,
    string? AudioCodec,
    long BitRate,
    double FrameRate,
    IReadOnlyDictionary<string, string>? FormatTags,
    string? ColorTransfer = null);

/// <summary>
/// Извлекает кадр-обложку и метаданные из видео через FFmpeg (FFMpegCore).
/// Бинарь ffmpeg/ffprobe берётся из каталога, заданного через GlobalFFOptions в Program.cs.
/// </summary>
public class VideoThumbnailExtractor
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(15);
    private readonly ILogger<VideoThumbnailExtractor> _logger;

    /// <summary>
    /// Момент кадра-обложки по умолчанию — 5-я секунда.
    /// </summary>
    public static readonly TimeSpan DefaultFramePosition = TimeSpan.FromSeconds(5);

    public VideoThumbnailExtractor(ILogger<VideoThumbnailExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Считывает размеры и длительность видео из файла на диске.
    /// </summary>
    public virtual async Task<(int Width, int Height, TimeSpan Duration)> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeout(cancellationToken, ProbeTimeout);
        var info = await FFProbe.AnalyseAsync(filePath, cancellationToken: timeout.Token);
        var video = info.PrimaryVideoStream;
        return (video?.Width ?? 0, video?.Height ?? 0, info.Duration);
    }

    /// <summary>
    /// Полные технические метаданные контейнера: размеры, длительность, кодеки, битрейт, fps
    /// плюс теги контейнера (для GPS/устройства из QuickTime/MP4).
    /// Используется для заполнения <see cref="Domain.FileMetadata"/>.
    /// </summary>
    public virtual async Task<VideoProbe> ProbeFullAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeout(cancellationToken, ProbeTimeout);
        var info = await FFProbe.AnalyseAsync(filePath, cancellationToken: timeout.Token);
        var video = info.PrimaryVideoStream;
        var audio = info.PrimaryAudioStream;

        var bitRate = info.Format?.BitRate > 0 ? (long)info.Format.BitRate : 0L;

        return new VideoProbe(
            Width: video?.Width ?? 0,
            Height: video?.Height ?? 0,
            Duration: info.Duration,
            VideoCodec: video?.CodecName,
            AudioCodec: audio?.CodecName,
            BitRate: bitRate,
            FrameRate: video?.FrameRate ?? 0,
            FormatTags: info.Format?.Tags,
            ColorTransfer: video?.ColorTransfer);
    }

    /// <summary>
    /// Извлекает один кадр в момент <paramref name="at"/> и возвращает его как JPEG-байты.
    /// Если видео короче запрошенного момента — берём середину. Кадр снимается во временный
    /// файл (без System.Drawing — надёжно на Linux), затем считывается и удаляется.
    /// </summary>
    public virtual async Task<byte[]> ExtractFrameJpegAsync(string filePath, TimeSpan at, CancellationToken cancellationToken = default)
    {
        var (_, _, duration) = await ProbeAsync(filePath, cancellationToken);
        var capture = duration > TimeSpan.Zero && at >= duration
            ? TimeSpan.FromTicks(duration.Ticks / 2)
            : at;

        var tempJpg = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        try
        {
            using var timeout = CreateTimeout(cancellationToken, FfmpegTimeout);
            var ok = await FFMpegArguments
                .FromFileInput(filePath, false, options => options.Seek(capture))
                .OutputToFile(tempJpg, true, options => options
                    .WithFrameOutputCount(1)
                    .ForceFormat("image2"))
                .CancellableThrough(timeout.Token)
                .ProcessAsynchronously();
            if (!ok || !File.Exists(tempJpg))
                throw new InvalidOperationException("FFmpeg не сгенерировал кадр-обложку");

            return await File.ReadAllBytesAsync(tempJpg, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempJpg))
                    File.Delete(tempJpg);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить временный кадр {TempJpg}", tempJpg);
            }
        }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken, TimeSpan duration)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        return timeout;
    }
}
