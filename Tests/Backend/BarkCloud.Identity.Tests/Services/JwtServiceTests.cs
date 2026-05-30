using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Shared.Identity;

using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace BarkCloud.Identity.Tests.Services;

public class JwtServiceTests
{
    private static JwtSettings BuildSettings(int expiryMinutes = 60) => new()
    {
        SecretKey = "supersecretkey_at_least_32_chars_long_for_hs256!!",
        Issuer = "bark-issuer",
        Audience = "bark-audience",
        ExpiryMinutes = expiryMinutes
    };

    private static JwtSecurityToken Read(string token)
        => new JwtSecurityTokenHandler().ReadJwtToken(token);

    [Fact]
    public void GenerateUserToken_ReturnsTokenWithUserAndDeviceClaims()
    {
        var sut = new JwtService(BuildSettings());

        var result = sut.GenerateUserToken(userId: 42, deviceId: "device-abc");

        var jwt = Read(result.Value);
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.UserId && c.Value == "42");
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.DeviceId && c.Value == "device-abc");
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.TokenType && c.Value == TokenType.User.ToString());
    }

    [Fact]
    public void GenerateUserToken_SetsIssuerAndAudienceFromSettings()
    {
        var sut = new JwtService(BuildSettings());

        var token = sut.GenerateUserToken(1, "d");

        var jwt = Read(token.Value);
        jwt.Issuer.Should().Be("bark-issuer");
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("bark-audience");
    }

    [Fact]
    public void GenerateUserToken_ExpiresAfterConfiguredMinutes()
    {
        var sut = new JwtService(BuildSettings(expiryMinutes: 30));
        var before = DateTime.UtcNow;

        var token = sut.GenerateUserToken(1, "d");

        var expected = before.AddMinutes(30);
        token.ExpirationDate.ToDateTime().Should().BeCloseTo(expected, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GenerateUserToken_TokenIsSignedWithHs256()
    {
        var sut = new JwtService(BuildSettings());

        var token = sut.GenerateUserToken(1, "d");

        var jwt = Read(token.Value);
        jwt.SignatureAlgorithm.Should().Be(SecurityAlgorithms.HmacSha256);
    }

    [Fact]
    public void GenerateUserToken_ProducesTokenValidatedByConfiguredKey()
    {
        var settings = BuildSettings();
        var sut = new JwtService(settings);

        var token = sut.GenerateUserToken(42, "device-abc");

        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token.Value, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SecretKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(5)
        }, out _);
    }

    [Fact]
    public void GenerateServerToken_ReturnsTokenWithServiceClaim()
    {
        var sut = new JwtService(BuildSettings());

        var token = sut.GenerateServerToken(ServiceId.Identity);

        var jwt = Read(token);
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.ServiceId && c.Value == ServiceId.Identity.ToString());
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.TokenType && c.Value == TokenType.Service.ToString());
    }

    [Fact]
    public void GenerateServerToken_HasFarFutureExpiration()
    {
        var sut = new JwtService(BuildSettings());

        var token = sut.GenerateServerToken(ServiceId.Files);

        var jwt = Read(token);
        jwt.ValidTo.Year.Should().Be(9999);
    }

    [Fact]
    public void GenerateServerToken_DoesNotContainUserOrDeviceClaims()
    {
        var sut = new JwtService(BuildSettings());

        var token = sut.GenerateServerToken(ServiceId.Files);

        var jwt = Read(token);
        jwt.Claims.Should().NotContain(c => c.Type == IdentityClaims.UserId);
        jwt.Claims.Should().NotContain(c => c.Type == IdentityClaims.DeviceId);
    }

    [Fact]
    public void GenerateUserToken_ValidationWithWrongKey_IsRejected()
    {
        var sut = new JwtService(BuildSettings());
        var token = sut.GenerateUserToken(1, "d");

        var handler = new JwtSecurityTokenHandler();
        var act = () => handler.ValidateToken(token.Value, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a_completely_different_secret_key_32bytes!!")),
            ValidateLifetime = false
        }, out _);

        act.Should().Throw<SecurityTokenException>();
    }
}
