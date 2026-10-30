using BarkCloud.Files.Services;

using Xunit;

namespace BarkCloud.Files.Tests.Services;

public sealed class VideoVariantLadderTests
{
    [Theory]
    [InlineData(1920, 1080, 1080)]
    [InlineData(1080, 1920, 1080)]
    [InlineData(3840, 2160, 2160)]
    public void GetShortSideSupportsLandscapeAndPortrait(int width, int height, int expected)
    {
        Assert.Equal(expected, VideoVariantLadder.GetShortSide(width, height));
    }

    [Fact]
    public void GetMissingHeightsExcludesOriginalHeightAndExistingOrSkippedVariants()
    {
        var heights = VideoVariantLadder.GetMissingHeights(1080, [360], [720]);

        Assert.Empty(heights);
    }

    [Fact]
    public void GetMissingHeightsFor1080ShortSideReturns360And720()
    {
        Assert.Equal(new[] { 360, 720 }, VideoVariantLadder.GetMissingHeights(1080, [], []));
    }

    [Fact]
    public void ShouldSkipVariantWhenItIsNotSmallerThanOriginal()
    {
        Assert.True(VideoVariantLadder.ShouldSkipBySize(100, 100));
        Assert.True(VideoVariantLadder.ShouldSkipBySize(100, 101));
        Assert.False(VideoVariantLadder.ShouldSkipBySize(100, 99));
    }
}
