using FFMpegCore;

namespace BarkCloud.Files.Services;

/// <summary>
/// Конвертирует HEIC/HEIF в JPEG через FFmpeg (FFMpegCore). ImageSharp не умеет
/// декодировать HEIC, поэтому такие фото проходят перекодирование тем же бинарём
/// ffmpeg, что используется для превью видео (см. <see cref="VideoThumbnailExtractor"/>).
/// Бинарь берётся из каталога, заданного через GlobalFFOptions в Program.cs.
/// </summary>
public class HeicImageConverter
{
    private readonly ILogger<HeicImageConverter> _logger;

    public HeicImageConverter(ILogger<HeicImageConverter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Перекодирует HEIC-файл на диске в JPEG и возвращает его байты.
    /// Берётся основной кадр изображения (-frames:v 1) с высоким качеством (-q:v 2).
    /// </summary>
    public virtual async Task<byte[]> ConvertToJpegAsync(string inputFilePath, CancellationToken cancellationToken = default)
    {
        var tempJpg = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        try
        {
            var ok = await FFMpegArguments
                .FromFileInput(inputFilePath)
                .OutputToFile(tempJpg, overwrite: true, options => options
                    .WithCustomArgument("-frames:v 1")
                    .WithCustomArgument("-q:v 2")
                    .ForceFormat("image2"))
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously();

            if (!ok || !File.Exists(tempJpg))
                throw new InvalidOperationException("FFmpeg не сконвертировал HEIC в JPEG");

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
                _logger.LogWarning(ex, "Не удалось удалить временный JPEG {TempJpg}", tempJpg);
            }
        }
    }
}
