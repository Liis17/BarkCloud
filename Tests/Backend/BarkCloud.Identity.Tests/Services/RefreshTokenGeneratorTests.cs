using BarkCloud.Identity.Services;

namespace BarkCloud.Identity.Tests.Services;

public class RefreshTokenGeneratorTests
{
    [Fact]
    public void GenerateRefreshToken_DoesNotContainStandardBase64UnsafeChars()
    {
        var token = RefreshTokenGenerator.GenerateRefreshToken();

        token.Should().NotContain("+");
        token.Should().NotContain("/");
        token.Should().NotEndWith("=");
    }

    [Fact]
    public void GenerateRefreshToken_ProducesUniqueTokens()
    {
        var tokens = Enumerable.Range(0, 100)
            .Select(_ => RefreshTokenGenerator.GenerateRefreshToken())
            .ToList();

        tokens.Distinct().Count().Should().Be(tokens.Count);
    }

    [Fact]
    public void GenerateRefreshToken_HasEnoughEntropy()
    {
        // 32 байта → 43 url-safe base64-символа (без паддинга).
        var token = RefreshTokenGenerator.GenerateRefreshToken();

        token.Length.Should().Be(43);
    }
}
