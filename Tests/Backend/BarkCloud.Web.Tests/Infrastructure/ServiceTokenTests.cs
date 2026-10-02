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
    [InlineData("test-secret-key-at-least-32-bytes-long!!")]
    [InlineData("СекретныйКлючДляПодписиТокеновЮникод")]
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

    [Fact]
    public void Generate_EmptySecret_ReturnsEmptyToken()
        => ServiceToken.Generate(Config(null)).Should().BeEmpty();

    [Fact]
    public void Generate_UnusableSecret_ThrowsConfigurationError()
    {
        var act = () => ServiceToken.Generate(Config("short"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*JwtSettings:SecretKey*");
    }
}
