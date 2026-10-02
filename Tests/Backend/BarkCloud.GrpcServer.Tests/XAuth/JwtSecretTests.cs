using BarkCloud.Shared.Identity;

using System.Text;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class JwtSecretTests
{
    private const string AsciiSecret = "supersecretkey_at_least_32_chars_long_for_hs256!!";

    [Fact]
    public void GetKeyBytes_AsciiSecret_MatchesLegacyAsciiBytes()
    {
        JwtSecret.GetKeyBytes(AsciiSecret).Should().Equal(Encoding.ASCII.GetBytes(AsciiSecret));
    }

    [Fact]
    public void GetKeyBytes_CyrillicSecret_ReturnsUtf8BytesNotQuestionMarks()
    {
        var secret = new string('ж', 20);

        var bytes = JwtSecret.GetKeyBytes(secret);

        bytes.Should().Equal(Encoding.UTF8.GetBytes(secret));
        bytes.Should().NotEqual(Encoding.ASCII.GetBytes(secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetKeyBytes_MissingSecret_ThrowsConfigurationError(string? secret)
    {
        var act = () => JwtSecret.GetKeyBytes(secret);

        act.Should().Throw<InvalidOperationException>().WithMessage("*JwtSettings:SecretKey не задан*");
    }

    [Fact]
    public void GetKeyBytes_ShorterThanStartupMinimum_ThrowsWithoutLeakingSecret()
    {
        const string secret = "short-secret";

        var act = () => JwtSecret.GetKeyBytes(secret);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("12").And.Contain("16").And.NotContain(secret);
    }

    [Theory]
    [InlineData(8, 16, true)]    // 16 байт — нижний предел старта
    [InlineData(7, 16, false)]   // 14 байт
    [InlineData(10, 16, true)]   // 20 байт проходит старт...
    [InlineData(10, 32, false)]  // ...но не рекомендованный минимум при записи
    [InlineData(16, 32, true)]   // 32 байта — рекомендованный минимум
    public void GetKeyBytes_ThresholdIsCountedInBytesNotCharacters(int cyrillicChars, int minBytes, bool accepted)
    {
        var secret = new string('ж', cyrillicChars);

        var act = () => JwtSecret.GetKeyBytes(secret, minBytes);

        if (accepted)
            act.Should().NotThrow();
        else
            act.Should().Throw<InvalidOperationException>();
    }
}
