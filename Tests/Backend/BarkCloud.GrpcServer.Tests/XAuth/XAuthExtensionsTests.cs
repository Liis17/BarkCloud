using BarkCloud.GrpcServer.XAuth;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class XAuthExtensionsTests
{
    private const string AsciiSecret = "supersecretkey_at_least_32_chars_long_for_hs256!!";

    private static IConfiguration Config(string? secret)
    {
        var values = new Dictionary<string, string?>
        {
            ["JwtSettings:Issuer"] = "bark-issuer",
            ["JwtSettings:Audience"] = "bark-audience"
        };
        if (secret is not null) values["JwtSettings:SecretKey"] = secret;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // Подпись как у Identity.JwtService: HS256 по UTF-8 байтам секрета.
    private static string IssueLikeIdentity(string secret)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "1")]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            Issuer = "bark-issuer",
            Audience = "bark-audience",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256)
        };
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    private static TokenValidationParameters ValidationFor(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddXAuth(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
    }

    [Theory]
    [InlineData(AsciiSecret)]
    [InlineData("СекретныйКлючДляПодписиТокеновЮникод")]
    public void AddXAuth_ValidatesTokenSignedLikeIdentity(string secret)
    {
        var parameters = ValidationFor(Config(secret));

        var act = () => new JwtSecurityTokenHandler().ValidateToken(IssueLikeIdentity(secret), parameters, out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddXAuth_RejectsTokenSignedWithOtherSecret()
    {
        var parameters = ValidationFor(Config("СекретныйКлючДляПодписиТокеновЮникод"));

        var act = () => new JwtSecurityTokenHandler()
            .ValidateToken(IssueLikeIdentity("ДругойСекретныйКлючДляПодписиТокенов"), parameters, out _);

        act.Should().Throw<SecurityTokenException>();
    }

    [Theory]
    [InlineData("short-secret")]
    [InlineData("жжжжжжж")]
    [InlineData(null)]
    public void AddXAuth_UnusableSecret_FailsAtRegistration(string? secret)
    {
        var act = () => new ServiceCollection().AddXAuth(Config(secret));

        act.Should().Throw<InvalidOperationException>().WithMessage("*JwtSettings:SecretKey*");
    }
}
