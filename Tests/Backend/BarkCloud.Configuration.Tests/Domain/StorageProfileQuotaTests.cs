using BarkCloud.Configuration.Domain;

namespace BarkCloud.Configuration.Tests.Domain;

public sealed class StorageProfileQuotaTests
{
    [Theory]
    [InlineData("3", "gb", 3_221_225_472L)]
    [InlineData("2", "tb", 2_199_023_255_552L)]
    [InlineData("2", "pb", 2_251_799_813_685_248L)]
    [InlineData("0", "pb", 0L)]
    public void ParseBytes_ConvertsBinaryIntegerUnits(string value, string unit, long expected)
    {
        StorageProfileQuota.ParseBytes(value, unit).Should().Be(expected);
    }

    [Theory]
    [InlineData("-1", "gb")]
    [InlineData("1.5", "gb")]
    [InlineData("1", "mb")]
    [InlineData("8192", "pb")]
    public void ParseBytes_RejectsNegativeFractionalUnknownOrOverflowingValues(string value, string unit)
    {
        var act = () => StorageProfileQuota.ParseBytes(value, unit);

        act.Should().Throw<InvalidOperationException>();
    }
}
