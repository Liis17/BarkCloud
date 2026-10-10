using BarkCloud.Shared.Identity;
using BarkCloud.Web.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace BarkCloud.Web.Tests.Infrastructure;

public class ServiceTokenTests
{
    private static IConfiguration Config(string? secret) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:SecretKey"] = secret,
            ["JwtSettings:Issuer"] = "bark",
            ["JwtSettings:Audience"] = "bark"
        })
        .Build();

    [Theory]
    [MemberData(nameof(ValidSecrets))]
    public void Generate_TokenIsValidatedByUtf8Key(string secret)
    {
        var token = ServiceToken.Generate(Config(secret));

        SecurityToken? validated = null;
        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidIssuer = "bark",
            ValidAudience = "bark",
            ValidateLifetime = true
        }, out validated);

        act.Should().NotThrow();
        ((JwtSecurityToken)validated!).Claims.Should()
            .Contain(c => c.Type == IdentityClaims.TokenType && c.Value == nameof(TokenType.Service));
    }

    public static TheoryData<string> ValidSecrets => new()
    {
        { "test-secret-key-at-least-32-bytes-long!!" },
        { "СекретныйКлючДляПодписиТокеновЮникод" },
        { new string('a', 32) },
        { new string('ж', 16) }
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Generate_EmptySecret_ReturnsEmptyToken(string? secret)
        => ServiceToken.Generate(Config(secret)).Should().BeEmpty();

    [Theory]
    [MemberData(nameof(UnusableSecrets))]
    public void Generate_UnusableSecret_ThrowsConfigurationErrorWithoutLeakingSecret(string secret)
    {
        var act = () => ServiceToken.Generate(Config(secret));

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("JwtSettings:SecretKey").And.NotContain(secret);
    }

    public static TheoryData<string> UnusableSecrets => new()
    {
        { new string('a', 31) },
        { new string('ж', 15) }
    };
}
