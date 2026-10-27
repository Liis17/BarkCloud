using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;

namespace BarkCloud.Identity.Tests.Services;

public class JwtServiceTests : IDisposable
{
    private readonly RsaJwtTestKeys _keys = new();

    private JwtSettings BuildSettings(int expiryMinutes = 60) => new()
    {
        Issuer = RsaJwtTestKeys.TestIssuer,
        Audience = RsaJwtTestKeys.TestAudience,
        ExpiryMinutes = expiryMinutes,
        PrivateKeyFile = _keys.PrivateKeyFile
    };

    private static JwtSecurityToken Read(string token)
        => new JwtSecurityTokenHandler().ReadJwtToken(token);

    [Fact]
    public void GenerateUserToken_ReturnsTokenWithUserAndDeviceClaims()
    {
        var sut = new JwtService(BuildSettings());

        var result = sut.GenerateUserToken(userId: 42, deviceId: "device-abc", sessionId: 9);

        var jwt = Read(result.Value);
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.UserId && c.Value == "42");
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.DeviceId && c.Value == "device-abc");
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.SessionId && c.Value == "9");
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.TokenType && c.Value == TokenType.User.ToString());
    }

    [Fact]
    public void GenerateUserToken_SetsIssuerAndAudienceFromSettings()
    {
        var sut = new JwtService(BuildSettings());

        var token = sut.GenerateUserToken(1, "d", 7);

        var jwt = Read(token.Value);
        jwt.Issuer.Should().Be(RsaJwtTestKeys.TestIssuer);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be(RsaJwtTestKeys.TestAudience);
    }

    [Fact]
    public void GenerateUserToken_ExpiresAfterConfiguredMinutes()
    {
        var sut = new JwtService(BuildSettings(expiryMinutes: 30));
        var before = DateTime.UtcNow;

        var token = sut.GenerateUserToken(1, "d", 7);

        var expected = before.AddMinutes(30);
        token.ExpirationDate.ToDateTime().Should().BeCloseTo(expected, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GenerateUserToken_TokenIsSignedWithRs256AndKid()
    {
        var sut = new JwtService(BuildSettings());

        var token = sut.GenerateUserToken(1, "d", 7);

        var jwt = Read(token.Value);
        jwt.SignatureAlgorithm.Should().Be(SecurityAlgorithms.RsaSha256);
        jwt.Header.Kid.Should().Be(_keys.Kid);
    }

    [Fact]
    public void GenerateUserToken_ProducesTokenValidatedByPublicKey()
    {
        var sut = new JwtService(BuildSettings());

        var token = sut.GenerateUserToken(42, "device-abc", 7);

        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token.Value, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = RsaJwtTestKeys.TestIssuer,
            ValidateAudience = true,
            ValidAudience = RsaJwtTestKeys.TestAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = RsaJwtKeys.LoadPublicKeyMap(_keys.PublicKeyFile).Values,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(5)
        }, out _);
    }

    [Fact]
    public void GenerateServiceToken_ReturnsCallerTargetAndPermissionsClaims()
    {
        var sut = new JwtService(BuildSettings());

        var (token, expiresAt) = sut.GenerateServiceToken(ServiceId.Torrent, ServiceId.Identity);

        var jwt = Read(token);
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.ServiceId && c.Value == ((int)ServiceId.Torrent).ToString());
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.TokenType && c.Value == TokenType.Service.ToString());
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.TargetService && c.Value == ((int)ServiceId.Identity).ToString());
        jwt.Claims.Should().Contain(c => c.Type == IdentityClaims.Permissions && c.Value == ServicePermissions.RevocationRead);
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.Add(JwtService.ServiceTokenLifetime), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GenerateServiceToken_WebGetsFullAdminPermissionSet()
    {
        var sut = new JwtService(BuildSettings());

        var (token, _) = sut.GenerateServiceToken(ServiceId.Web, ServiceId.Files);

        var jwt = Read(token);
        var permissions = jwt.Claims.Single(c => c.Type == IdentityClaims.Permissions).Value;
        permissions.Split(' ').Should().BeEquivalentTo(ServicePermissions.For(ServiceId.Web));
    }

    [Fact]
    public void GenerateServiceToken_LifetimeAboutTenMinutes()
    {
        var sut = new JwtService(BuildSettings());

        var (token, _) = sut.GenerateServiceToken(ServiceId.Files, ServiceId.Users);

        var jwt = Read(token);
        jwt.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GenerateServiceToken_DoesNotContainUserOrDeviceClaims()
    {
        var sut = new JwtService(BuildSettings());

        var (token, _) = sut.GenerateServiceToken(ServiceId.Files, ServiceId.Users);

        var jwt = Read(token);
        jwt.Claims.Should().NotContain(c => c.Type == IdentityClaims.UserId);
        jwt.Claims.Should().NotContain(c => c.Type == IdentityClaims.DeviceId);
    }

    // Подпись Identity (RS256) обязана проходить проверку XAuth остальных сервисов по публичному ключу.
    [Fact]
    public void GeneratedTokens_AreAcceptedByXAuthValidation()
    {
        var sut = new JwtService(BuildSettings());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RsaJwtKeys.PublicKeyConfigKey] = _keys.PublicKeyFile,
            ["JwtSettings:Issuer"] = RsaJwtTestKeys.TestIssuer,
            ["JwtSettings:Audience"] = RsaJwtTestKeys.TestAudience
        }).Build();
        var services = new ServiceCollection();
        services.AddXAuth(configuration, ServiceId.Files);
        using var provider = services.BuildServiceProvider();
        var parameters = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        var handler = new JwtSecurityTokenHandler();

        var userToken = () => handler.ValidateToken(sut.GenerateUserToken(42, "device-abc", 7).Value, parameters, out _);
        var serviceToken = () => handler.ValidateToken(
            sut.GenerateServiceToken(ServiceId.Identity, ServiceId.Files).Token, parameters, out _);

        userToken.Should().NotThrow();
        serviceToken.Should().NotThrow();
    }

    [Fact]
    public void MissingPrivateKey_FailsWithConfigHint_WithoutLeakingValue()
    {
        var settings = BuildSettings();
        settings.PrivateKeyFile = null;
        Environment.SetEnvironmentVariable(RsaJwtKeys.PrivateKeyEnvName, null);
        var sut = new JwtService(settings);

        var userMessage = Record.Exception(() => sut.GenerateUserToken(42, "device-abc", 7))
            .Should().BeOfType<InvalidOperationException>().Which.Message;
        var serviceMessage = Record.Exception(() => sut.GenerateServiceToken(ServiceId.Files, ServiceId.Users))
            .Should().BeOfType<InvalidOperationException>().Which.Message;

        userMessage.Should().Contain(RsaJwtKeys.PrivateKeyConfigKey).And.Contain(RsaJwtKeys.PrivateKeyEnvName);
        serviceMessage.Should().Contain(RsaJwtKeys.PrivateKeyConfigKey).And.Contain(RsaJwtKeys.PrivateKeyEnvName);
    }

    [Fact]
    public void GenerateUserToken_ValidationWithOtherKey_IsRejected()
    {
        var sut = new JwtService(BuildSettings());
        var token = sut.GenerateUserToken(1, "d", 7);

        using var otherKeys = new RsaJwtTestKeys();
        var handler = new JwtSecurityTokenHandler();
        var act = () => handler.ValidateToken(token.Value, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = RsaJwtKeys.LoadPublicKeyMap(otherKeys.PublicKeyFile).Values,
            ValidateLifetime = false
        }, out _);

        act.Should().Throw<SecurityTokenException>();
    }

    public void Dispose() => _keys.Dispose();
}
