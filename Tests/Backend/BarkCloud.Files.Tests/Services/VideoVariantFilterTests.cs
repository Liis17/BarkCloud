using BarkCloud.Files.Services;

using Xunit;

namespace BarkCloud.Files.Tests.Services;

public sealed class VideoVariantFilterTests
{
    [Fact]
    public void GetSkipReasonAllowsValuesAtConfiguredLimits()
    {
        Assert.Null(VideoVariantFilter.GetSkipReason(1024 * 1024, 60, 1, 1));
    }

    [Fact]
    public void GetSkipReasonRejectsSourceOverSizeLimit()
    {
        Assert.Contains("размер", VideoVariantFilter.GetSkipReason(1024 * 1024 + 1, 60, 1, 1)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSkipReasonRejectsUnknownDurationWhenLimitIsSet()
    {
        Assert.Contains("неизвестна", VideoVariantFilter.GetSkipReason(1, null, null, 5)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSkipReasonRejectsDurationOverLimit()
    {
        Assert.Contains("длительность", VideoVariantFilter.GetSkipReason(1, 301, null, 5)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSkipReasonTreatsEmptyLimitsAsUnlimited()
    {
        Assert.Null(VideoVariantFilter.GetSkipReason(long.MaxValue, null, null, null));
    }
}
