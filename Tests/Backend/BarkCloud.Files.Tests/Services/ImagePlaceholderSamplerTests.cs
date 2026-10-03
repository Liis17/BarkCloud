using BarkCloud.Files.Services;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace BarkCloud.Files.Tests.Services;

public class ImagePlaceholderSamplerTests
{
    private static readonly string[] GridColors =
    [
        "#FF0000", "#00FF00", "#0000FF",
        "#FFFF00", "#00FFFF", "#FF00FF",
        "#FFFFFF", "#808080", "#000000"
    ];

    [Theory]
    [InlineData(180, 90)]
    [InlineData(90, 180)]
    public async Task Photo_SamplesCentralSquareInRowMajorOrder(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, Color.Orange.ToPixel<Rgba32>());
        PaintGrid(image, (width - 90) / 2, (height - 90) / 2, 90, 90);

        var sample = await new ImagePlaceholderSampler().SampleAsync(Encode(image), squareCrop: true);

        sample.Colors.Should().Equal(GridColors);
        sample.AspectRatio.Should().Be(1);
    }

    [Fact]
    public async Task Photo_AppliesExifOrientationBeforeSampling()
    {
        using var image = new Image<Rgba32>(180, 90, Color.Orange.ToPixel<Rgba32>());
        PaintGrid(image, 45, 0, 90, 90);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);

        var sample = await new ImagePlaceholderSampler().SampleAsync(Encode(image), squareCrop: true);

        sample.Colors.Should().Equal(
            GridColors[6], GridColors[3], GridColors[0],
            GridColors[7], GridColors[4], GridColors[1],
            GridColors[8], GridColors[5], GridColors[2]);
    }

    [Fact]
    public async Task Video_SamplesWholeWidePreview()
    {
        using var image = new Image<Rgba32>(160, 90);
        PaintGrid(image, 0, 0, 160, 90);

        var sample = await new ImagePlaceholderSampler().SampleAsync(Encode(image), squareCrop: false);

        sample.Colors.Should().Equal(GridColors);
        sample.AspectRatio.Should().BeApproximately(16f / 9, 0.0001f);
    }

    [Fact]
    public async Task Sample_AveragesFiveByFiveInsteadOfSinglePixel()
    {
        using var image = new Image<Rgba32>(30, 30, new Rgba32(0, 255, 0));
        for (var y = 3; y <= 7; y++)
        for (var x = 3; x <= 7; x++)
            image[x, y] = new Rgba32(10, 20, 30);
        image[5, 5] = new Rgba32(255, 0, 55);

        var sample = await new ImagePlaceholderSampler().SampleAsync(Encode(image), squareCrop: true);

        sample.Colors[0].Should().Be("#14131F");
        sample.Colors.Skip(1).Should().OnlyContain(c => c == "#00FF00");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)]
    public async Task Photo_ClampsTinySamplingRegionsToCentralCrop(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, Color.Red.ToPixel<Rgba32>());
        var side = Math.Min(width, height);
        for (var y = 0; y < side; y++)
        for (var x = (width - side) / 2; x < (width + side) / 2; x++)
            image[x, y] = new Rgba32(17, 34, 51);

        var sample = await new ImagePlaceholderSampler().SampleAsync(Encode(image), squareCrop: true);

        sample.Colors.Should().Equal(Enumerable.Repeat("#112233", 9));
    }

    private static void PaintGrid(Image<Rgba32> image, int left, int top, int width, int height)
    {
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            var color = Color.ParseHex(GridColors[row * 3 + column]).ToPixel<Rgba32>();
            for (var y = top + row * height / 3; y < top + (row + 1) * height / 3; y++)
            for (var x = left + column * width / 3; x < left + (column + 1) * width / 3; x++)
                image[x, y] = color;
        }
    }

    private static byte[] Encode(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
