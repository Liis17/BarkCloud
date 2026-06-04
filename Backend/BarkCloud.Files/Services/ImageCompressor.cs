using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BarkCloud.Files.Services;

public partial class ImageCompressor
{
    /// <summary>
    /// Максимальная сторона изображения (по дизайн-документу).
    /// </summary>
    private const int MaxOriginalSide = 2500;

    /// <summary>
    /// Максимальный размер изображения в байтах (2 МБ).
    /// </summary>
    private const long MaxOriginalSizeBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Качество JPEG для принудительного сжатия оригинала (по дизайн-документу).
    /// </summary>
    private const int OriginalJpegQuality = 90;

    /// <summary>
    /// Качество JPEG для превью (thumbnail).
    /// </summary>
    private const int PreviewJpegQuality = 75;

    /// <summary>
    /// Генерация превью (thumbnail) для быстрой загрузки.
    /// </summary>
    public async Task<byte[]> CompressImageAsync(Stream inputStream, int width = 1024)
    {
        using var image = await Image.LoadAsync(inputStream);

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(width, 0)
        }));

        // Композитинг на белый фон — JPEG не поддерживает альфа-канал,
        // без этого прозрачные области дают чёрные/искажённые пиксели
        image.Mutate(x => x.BackgroundColor(Color.White));

        using var outputStream = new MemoryStream();
        var encoder = new JpegEncoder { Quality = PreviewJpegQuality };
        await image.SaveAsync(outputStream, encoder);

        return outputStream.ToArray();
    }

    /// <summary>
    /// Принудительное сжатие оригинала по требованиям дизайн-документа:
    /// - Макс. сторона: 2500 px
    /// - Макс. размер: 2 МБ
    /// - Формат: JPEG 90%
    /// Возвращает (сжатые байты, true) если сжатие было применено, иначе (null, false).
    /// </summary>
    public async Task<(byte[]? CompressedBytes, bool WasCompressed)> EnforceOriginalLimitsAsync(Stream inputStream)
    {
        var streamLength = inputStream.Length;

        using var image = await Image.LoadAsync(inputStream);

        var needsResize = image.Width > MaxOriginalSide || image.Height > MaxOriginalSide;
        var needsCompress = streamLength > MaxOriginalSizeBytes;

        if (!needsResize && !needsCompress)
            return (null, false);

        if (needsResize)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(MaxOriginalSide, MaxOriginalSide)
            }));
        }

        image.Mutate(x => x.BackgroundColor(Color.White));

        using var outputStream = new MemoryStream();
        var encoder = new JpegEncoder { Quality = OriginalJpegQuality };
        await image.SaveAsync(outputStream, encoder);

        return (outputStream.ToArray(), true);
    }

    /// <summary>
    /// Обработка аватарки: ресайз до maxSide и сохранение как JPEG.
    /// Используется для серверной обработки вместо клиентского Canvas,
    /// который теряет ICC-профили и искажает цвета.
    /// </summary>
    public async Task<byte[]> ProcessAvatarAsync(Stream inputStream, int maxSide = 1500, int quality = 85)
    {
        using var image = await Image.LoadAsync(inputStream);

        if (image.Width > maxSide || image.Height > maxSide)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxSide, maxSide)
            }));
        }

        // Белый фон для JPEG (альфа-канал PNG/WebP → белый, а не чёрный)
        image.Mutate(x => x.BackgroundColor(Color.White));

        using var outputStream = new MemoryStream();
        var encoder = new JpegEncoder { Quality = quality };
        await image.SaveAsync(outputStream, encoder);

        return outputStream.ToArray();
    }

    /// <summary>
    /// Полноразмерный JPEG заданного качества (без ресайза) — для «JpegView»:
    /// браузеро-дружелюбное представление изображений, оригинал которых не JPEG
    /// (HEIC/PNG/WebP/…). Альфа-канал композитится на белый фон.
    /// </summary>
    public virtual async Task<byte[]> EncodeFullJpegAsync(
        Stream inputStream, int quality = 90, CancellationToken cancellationToken = default)
    {
        using var image = await Image.LoadAsync(inputStream, cancellationToken);
        image.Mutate(x => x.BackgroundColor(Color.White));

        using var ms = new MemoryStream();
        await image.SaveAsync(ms, new JpegEncoder { Quality = quality }, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>
    /// Объединённая обработка изображения за один <c>Image.LoadAsync</c>:
    /// возвращает размеры, опционально сжатый оригинал (если включён enforceOriginalLimits)
    /// и опционально превью (если задан previewWidth).
    /// Заменяет связку <see cref="EnforceOriginalLimitsAsync"/> + <see cref="CompressImageAsync"/>
    /// + <c>Image.IdentifyAsync</c>, делавшую несколько полных декодирований.
    /// </summary>
    public virtual async Task<ImageProcessingResult> ProcessImageAllInOneAsync(
        Stream inputStream,
        bool enforceOriginalLimits,
        int? previewWidth,
        CancellationToken cancellationToken = default)
    {
        var inputLength = inputStream.CanSeek ? inputStream.Length : 0L;

        using var image = await Image.LoadAsync(inputStream, cancellationToken);

        byte[]? compressedOriginal = null;

        if (enforceOriginalLimits)
        {
            var needsResize = image.Width > MaxOriginalSide || image.Height > MaxOriginalSide;
            var needsCompress = inputLength > MaxOriginalSizeBytes;

            if (needsResize || needsCompress)
            {
                if (needsResize)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(MaxOriginalSide, MaxOriginalSide)
                    }));
                }

                image.Mutate(x => x.BackgroundColor(Color.White));

                using var origStream = new MemoryStream();
                await image.SaveAsync(origStream, new JpegEncoder { Quality = OriginalJpegQuality }, cancellationToken);
                compressedOriginal = origStream.ToArray();
            }
        }

        byte[]? previewBytes = null;
        if (previewWidth.HasValue)
        {
            // Превью генерируется из текущего состояния image (уже ресайзнутого, если был enforce) —
            // это ожидаемое поведение: превью описывает то, что реально лежит в S3.
            using var preview = image.Clone(x => x
                .Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(previewWidth.Value, 0) })
                .BackgroundColor(Color.White));

            using var previewStream = new MemoryStream();
            await preview.SaveAsync(previewStream, new JpegEncoder { Quality = PreviewJpegQuality }, cancellationToken);
            previewBytes = previewStream.ToArray();
        }

        return new ImageProcessingResult(
            CompressedOriginal: compressedOriginal,
            PreviewBytes: previewBytes,
            Width: image.Width,
            Height: image.Height
        );
    }
}

public record ImageProcessingResult(
    byte[]? CompressedOriginal,
    byte[]? PreviewBytes,
    int Width,
    int Height);

/// <summary>
/// Один сгенерированный preview в составе мультиразмерного набора.
/// </summary>
public record MultiPreviewItem(int TargetWidth, int ActualWidth, int ActualHeight, byte[] Bytes);

public partial class ImageCompressor
{
    /// <summary>
    /// Генерация набора превью разных размеров за один <c>Image.LoadAsync</c>.
    /// Если оригинал по ширине ≤ targetWidth — это превью пропускается
    /// (ResizeMode.Max не увеличивает; держать превью «как оригинал» нет смысла).
    /// Все превью кодируются JPEG Quality=75 с белым фоном под альфа-канал.
    /// </summary>
    public virtual async Task<List<MultiPreviewItem>> GenerateMultiplePreviewsAsync(
        Stream inputStream,
        int[] targetWidths,
        CancellationToken cancellationToken = default)
    {
        var result = new List<MultiPreviewItem>();
        if (targetWidths is null || targetWidths.Length == 0)
            return result;

        using var image = await Image.LoadAsync(inputStream, cancellationToken);

        var originalWidth = image.Width;

        // Сортируем по убыванию ширины, чтобы итоговый порядок был стабильным.
        // Дедуплицируем на случай дубликатов на входе.
        var widths = targetWidths
            .Where(w => w > 0)
            .Distinct()
            .OrderByDescending(w => w)
            .ToArray();

        foreach (var targetWidth in widths)
        {
            if (originalWidth <= targetWidth)
                continue;

            using var preview = image.Clone(x => x
                .Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(targetWidth, 0) })
                .BackgroundColor(Color.White));

            using var ms = new MemoryStream();
            await preview.SaveAsync(ms, new JpegEncoder { Quality = PreviewJpegQuality }, cancellationToken);

            result.Add(new MultiPreviewItem(
                TargetWidth: targetWidth,
                ActualWidth: preview.Width,
                ActualHeight: preview.Height,
                Bytes: ms.ToArray()));
        }

        return result;
    }
}