using BarkCloud.Files.Exceptions;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BarkCloud.Files.Services;

public sealed class ImagePlaceholderSampler
{
    public async Task<(string[] Colors, float AspectRatio)> SampleAsync(
        byte[] previewBytes, bool squareCrop, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(previewBytes, writable: false);
        var info = await Image.IdentifyAsync(stream, cancellationToken);
        if ((long)info.Width * info.Height > ImageCompressor.MaximumDecodedPixels)
            throw new FileIntegrityException("Изображение превышает лимит декодирования 200 MP.");

        stream.Position = 0;
        using var image = await Image.LoadAsync<Rgba32>(stream, cancellationToken);
        image.Mutate(x => x.AutoOrient());

        var width = squareCrop ? Math.Min(image.Width, image.Height) : image.Width;
        var height = squareCrop ? width : image.Height;
        var left = (image.Width - width) / 2;
        var top = (image.Height - height) / 2;
        var colors = new string[9];

        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var centerX = left + (2 * column + 1) * width / 6;
            var centerY = top + (2 * row + 1) * height / 6;
            var red = 0;
            var green = 0;
            var blue = 0;
            var count = 0;

            for (var y = Math.Max(top, centerY - 2); y <= Math.Min(top + height - 1, centerY + 2); y++)
            for (var x = Math.Max(left, centerX - 2); x <= Math.Min(left + width - 1, centerX + 2); x++)
            {
                var pixel = image[x, y];
                red += pixel.R;
                green += pixel.G;
                blue += pixel.B;
                count++;
            }

            colors[row * 3 + column] =
                $"#{Mean(red, count):X2}{Mean(green, count):X2}{Mean(blue, count):X2}";
        }

        return (colors, (float)width / height);
    }

    private static int Mean(int sum, int count) =>
        (int)Math.Round((double)sum / count, MidpointRounding.AwayFromZero);
}
