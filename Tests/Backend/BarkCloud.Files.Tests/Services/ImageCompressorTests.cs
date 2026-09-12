using System.Buffers.Binary;

using BarkCloud.Files.Exceptions;
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

    private static MemoryStream CreateVerticalSplitImageStream()
    {
        using var image = new Image<Rgba32>(100, 200);
        var topColor = new Rgba32(255, 0, 0);
        var bottomColor = new Rgba32(0, 0, 255);

        for (var y = 0; y < image.Height; y++)
        {
            var color = y < image.Height / 2 ? topColor : bottomColor;
            for (var x = 0; x < image.Width; x++)
                image[x, y] = color;
        }

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

    [Fact]
    public async Task GenerateVideoPreviews_ProducesLandscapeSixteenByNineImages()
    {
        using var input = CreateImageStream(720, 1280);

        var previews = await _sut.GenerateVideoPreviewsAsync(input, [1024, 512, 128]);

        previews.Select(p => (p.TargetWidth, p.ActualWidth, p.ActualHeight))
            .Should().Equal(
                (1024, 1024, 576),
                (512, 512, 288),
                (128, 128, 72));

        foreach (var preview in previews)
        {
            using var result = Image.Load(preview.Bytes);
            result.Width.Should().Be(preview.ActualWidth);
            result.Height.Should().Be(preview.ActualHeight);
        }
    }

    [Fact]
    public async Task GenerateVideoPreviews_PreservesForegroundAndBlursBackground()
    {
        using var input = CreateVerticalSplitImageStream();

        var previews = await _sut.GenerateVideoPreviewsAsync(input, [320]);

        using var result = Image.Load<Rgba32>(previews.Single().Bytes);
        var centerX = result.Width / 2;
        var centerTop = result[centerX, result.Height / 4];
        var centerBottom = result[centerX, result.Height * 3 / 4];
        var edgeTop = result[0, result.Height / 4];
        var edgeBottom = result[0, result.Height * 3 / 4];
        var edgeBoundary = result[0, result.Height / 2];

        ((int)centerTop.R).Should().BeGreaterThan((int)centerTop.B + 100);
        ((int)centerBottom.B).Should().BeGreaterThan((int)centerBottom.R + 100);
        ((int)centerTop.R).Should().BeGreaterThan((int)edgeTop.R + 40);
        ((int)centerBottom.B).Should().BeGreaterThan((int)edgeBottom.B + 40);
        edgeBoundary.R.Should().BeGreaterThan(20);
        edgeBoundary.B.Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task GenerateVideoPreviews_CompositesTransparentSourceOnOpaqueBackground()
    {
        using var image = new Image<Rgba32>(100, 200, new Rgba32(0, 0, 0, 0));
        image[50, 100] = new Rgba32(255, 255, 255, 255);
        using var input = new MemoryStream();
        image.SaveAsPng(input);
        input.Position = 0;

        var previews = await _sut.GenerateVideoPreviewsAsync(input, [160]);

        using var result = Image.Load<Rgba32>(previews.Single().Bytes);
        var edge = result[0, result.Height / 2];

        edge.A.Should().Be(255);
        edge.R.Should().BeLessThan(10);
        edge.G.Should().BeLessThan(10);
        edge.B.Should().BeLessThan(10);
    }

    [Fact]
    public async Task ProcessImageAllInOne_WhenHeaderExceedsTwoHundredMegapixels_RejectsBeforeDecode()
    {
        using var input = OversizedBitmapHeader(width: 20_001, height: 10_000);

        var act = () => _sut.ProcessImageAllInOneAsync(input, false, null);

        await act.Should().ThrowAsync<FileIntegrityException>()
            .WithMessage("*200 MP*");
    }

    private static MemoryStream OversizedBitmapHeader(int width, int height)
    {
        var header = new byte[54];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(28), 24);
        return new MemoryStream(header);
    }
}
