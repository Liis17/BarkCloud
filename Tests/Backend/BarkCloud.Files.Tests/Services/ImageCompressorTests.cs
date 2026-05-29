using BarkCloud.Files.Services;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BarkCloud.Files.Tests.Services;

public class ImageCompressorTests
{
    private readonly ImageCompressor _sut = new();

    private static MemoryStream CreateImageStream(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        var ms = new MemoryStream();
        image.SaveAsPng(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task EnforceOriginalLimits_SmallImage_NotCompressed()
    {
        using var input = CreateImageStream(100, 100);

        var (bytes, wasCompressed) = await _sut.EnforceOriginalLimitsAsync(input);

        wasCompressed.Should().BeFalse();
        bytes.Should().BeNull();
    }

    [Fact]
    public async Task EnforceOriginalLimits_OversizedImage_ResizesWithinMaxSide()
    {
        using var input = CreateImageStream(3000, 2000);

        var (bytes, wasCompressed) = await _sut.EnforceOriginalLimitsAsync(input);

        wasCompressed.Should().BeTrue();
        bytes.Should().NotBeNull();

        using var result = Image.Load(bytes!);
        result.Width.Should().BeLessThanOrEqualTo(2500);
        result.Height.Should().BeLessThanOrEqualTo(2500);
    }

    [Fact]
    public async Task CompressImage_ProducesPreviewWithinRequestedWidth()
    {
        using var input = CreateImageStream(2000, 1000);

        var bytes = await _sut.CompressImageAsync(input, width: 1024);

        using var result = Image.Load(bytes);
        result.Width.Should().BeLessThanOrEqualTo(1024);
    }

    [Fact]
    public async Task GenerateMultiplePreviews_SkipsWidthsLargerThanOriginal_AndSortsDescending()
    {
        using var input = CreateImageStream(2000, 1000);

        var previews = await _sut.GenerateMultiplePreviewsAsync(input, [1024, 512, 4096]);

        previews.Select(p => p.TargetWidth).Should().Equal(1024, 512);
        previews.Should().OnlyContain(p => p.ActualWidth <= p.TargetWidth);
    }
}
