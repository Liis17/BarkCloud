using BarkCloud.Files.Services;

namespace BarkCloud.Files.Tests.Services;

public class SearchTextTests
{
    [Fact]
    public void Normalize_CollapsesWhitespace_NormalizesUnicodeAndLowercases()
    {
        var result = SearchText.Normalize("  МОЯ\tДЕВУШКА  ");

        result.Should().Be("моя девушка");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("a", false)]
    [InlineData("на", true)]
    public void IsSearchableQuery_RequiresAtLeastTwoNormalizedCharacters(string value, bool expected)
    {
        SearchText.IsSearchableQuery(value).Should().Be(expected);
    }
}
