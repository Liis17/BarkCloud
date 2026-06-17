using System.Diagnostics;

using BarkCloud.Files.Domain;

using FFMpegCore;

namespace BarkCloud.Files.Services;

public record AudioProbe(
    TimeSpan Duration,
    string? AudioCodec,
    long BitRate,
    IReadOnlyDictionary<string, string>? FormatTags);

/// <summary>
/// Извлекает технические аудио-метаданные и embedded artwork через ffprobe/ffmpeg.
/// </summary>
public class AudioMetadataExtractor
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AudioMetadataExtractor> _logger;

    public AudioMetadataExtractor(IConfiguration configuration, ILogger<AudioMetadataExtractor> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public virtual async Task<AudioProbe> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var info = await FFProbe.AnalyseAsync(filePath, cancellationToken: cancellationToken);
        var audio = info.PrimaryAudioStream;
        var bitRate = info.Format?.BitRate > 0 ? (long)info.Format.BitRate : 0L;

        return new AudioProbe(
            Duration: info.Duration,
            AudioCodec: audio?.CodecName,
            BitRate: bitRate,
            FormatTags: info.Format?.Tags);
    }

    public virtual FileMetadata ExtractMetadata(AudioProbe probe)
    {
        var tags = NormalizeTags(probe.FormatTags);

        return new FileMetadata
        {
            DurationSeconds = probe.Duration.TotalSeconds > 0 ? probe.Duration.TotalSeconds : null,
            AudioCodec = probe.AudioCodec,
            Bitrate = probe.BitRate > 0 ? probe.BitRate : null,
            AudioTitle = GetTag(tags, "title"),
            AudioArtist = GetTag(tags, "artist", "album_artist", "albumartist"),
            AudioAlbum = GetTag(tags, "album"),
            AudioTrackNumber = ParseTrackNumber(GetTag(tags, "track", "tracknumber"))
        };
    }

    public virtual async Task<byte[]?> ExtractArtworkJpegAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var tempJpg = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        try
        {
            var ffmpeg = ResolveFfmpegPath();
            var args = $"-y -i \"{filePath}\" -an -frames:v 1 -q:v 2 \"{tempJpg}\"";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || !File.Exists(tempJpg))
            {
                var stderr = await stderrTask;
                _logger.LogDebug("Embedded artwork не найдено для {FilePath}: {FfmpegError}", filePath, stderr);
                return null;
            }

            return await File.ReadAllBytesAsync(tempJpg, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось извлечь embedded artwork из {FilePath}", filePath);
            return null;
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
                _logger.LogWarning(ex, "Не удалось удалить временную аудио-обложку {TempJpg}", tempJpg);
            }
        }
    }

    private string ResolveFfmpegPath()
    {
        var folder = _configuration["Ffmpeg:BinaryFolder"];
        if (string.IsNullOrWhiteSpace(folder))
            return "ffmpeg";

        var linuxPath = Path.Combine(folder, "ffmpeg");
        if (File.Exists(linuxPath))
            return linuxPath;

        var windowsPath = Path.Combine(folder, "ffmpeg.exe");
        return File.Exists(windowsPath) ? windowsPath : "ffmpeg";
    }

    private static Dictionary<string, string> NormalizeTags(IReadOnlyDictionary<string, string>? tags)
    {
        return tags?.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value)
            ?? new Dictionary<string, string>();
    }

    private static string? GetTag(Dictionary<string, string> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static int? ParseTrackNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var firstPart = value.Split('/')[0].Trim();
        return int.TryParse(firstPart, out var track) ? track : null;
    }
}
