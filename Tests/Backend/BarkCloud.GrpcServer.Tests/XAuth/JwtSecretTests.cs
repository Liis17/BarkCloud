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
        bytes.Should().HaveCount(40);
        bytes.Should().NotEqual(Encoding.ASCII.GetBytes(secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetKeyBytes_MissingSecret_ThrowsConfigurationError(string? secret)
    {
        var act = () => JwtSecret.GetKeyBytes(secret);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("JwtSettings:SecretKey не задан").And.Contain("32");
    }

    [Fact]
    public void GetKeyBytes_ShorterThanMinimum_ThrowsWithoutLeakingSecret()
    {
        const string secret = "short-secret";

        var act = () => JwtSecret.GetKeyBytes(secret);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("12").And.Contain("32").And.NotContain(secret);
    }

    [Theory]
    [MemberData(nameof(ThresholdCases))]
    public void GetKeyBytes_ThresholdIsCountedInBytesNotCharacters(string secret, bool accepted)
    {
        var act = () => JwtSecret.GetKeyBytes(secret);

        if (accepted)
            act.Should().NotThrow();
        else
            act.Should().Throw<InvalidOperationException>();
    }

    public static TheoryData<string, bool> ThresholdCases => new()
    {
        { new string('a', 31), false },
        { new string('a', 32), true },
        { new string('ж', 15), false },
        { new string('ж', 16), true }
    };
}
