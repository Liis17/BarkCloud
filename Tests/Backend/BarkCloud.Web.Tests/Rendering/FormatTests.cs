using BarkCloud.Web.Rendering;

namespace BarkCloud.Web.Tests.Rendering;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(-5, "0 Б")]
    [InlineData(512, "512 Б")]
    [InlineData(1024, "1 КБ")]
    [InlineData(1048576, "1 МБ")]
    [InlineData(5368709120, "5 ГБ")]
    public void Size_FormatsWholeUnits(long bytes, string expected)
        => Format.Size(bytes).Should().Be(expected);

    [Theory]
    [InlineData(50, 100, 50)]
    [InlineData(0, 0, 0)]
    [InlineData(200, 100, 100)]
    [InlineData(1, 3, 33)]
    public void Percent_ClampsBetweenZeroAndHundred(long used, long total, int expected)
        => Format.Percent(used, total).Should().Be(expected);

    [Theory]
    [InlineData("Bark", "Dog", "BD")]
    [InlineData("bark", "dog", "BD")]
    [InlineData("", "", "?")]
    [InlineData("Solo", "", "S")]
    public void Initials_BuildsUppercaseInitials(string first, string last, string expected)
        => Format.Initials(first, last).Should().Be(expected);

    [Fact]
    public void Relative_JustNow_ForRecentTimestamp()
        => Format.Relative(DateTimeOffset.UtcNow).Should().Be("только что");

    [Fact]
    public void Relative_FutureTimestamp_TreatedAsJustNow()
        => Format.Relative(DateTimeOffset.UtcNow.AddHours(1)).Should().Be("только что");

    [Fact]
    public void Relative_HoursAgo()
        => Format.Relative(DateTimeOffset.UtcNow.AddHours(-2)).Should().Be("2 ч назад");

    [Fact]
    public void Relative_DaysAgo()
        => Format.Relative(DateTimeOffset.UtcNow.AddDays(-3)).Should().Be("3 дн назад");
}
