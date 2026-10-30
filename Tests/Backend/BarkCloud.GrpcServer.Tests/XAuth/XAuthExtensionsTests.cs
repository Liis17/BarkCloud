using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class XAuthExtensionsTests : IDisposable
{
    private readonly RsaJwtTestKeys _keys = new();

    private static IConfiguration Config(string publicKeyFile) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:Issuer"] = RsaJwtTestKeys.TestIssuer,
            ["JwtSettings:Audience"] = RsaJwtTestKeys.TestAudience,
            [RsaJwtKeys.PublicKeyConfigKey] = publicKeyFile
        }).Build();

    private static TokenValidationParameters ValidationFor(IConfiguration configuration, ServiceId serviceId)
    {
        var services = new ServiceCollection();
        services.AddXAuth(configuration, serviceId);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
    }

    [Fact]
    public void AddXAuth_ValidatesUserTokenSignedLikeIdentity()
    {
        var parameters = ValidationFor(Config(_keys.PublicKeyFile), ServiceId.Files);

        var act = () => new JwtSecurityTokenHandler()
            .ValidateToken(_keys.IssueToken(RsaJwtTestKeys.UserClaims()), parameters, out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddXAuth_RejectsTokenSignedWithOtherKey()
    {
        using var otherKeys = new RsaJwtTestKeys();
        var parameters = ValidationFor(Config(_keys.PublicKeyFile), ServiceId.Files);

        var act = () => new JwtSecurityTokenHandler()
            .ValidateToken(otherKeys.IssueToken(RsaJwtTestKeys.UserClaims()), parameters, out _);

        act.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void AddXAuth_RejectsHmacSignedToken()
    {
        var parameters = ValidationFor(Config(_keys.PublicKeyFile), ServiceId.Files);

        // Старая схема: HS256 по общему секрету. Должна отвергаться разрешённым алгоритмом.
        var hmac = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("hmac-secret-at-least-32-bytes-long!!")),
            SecurityAlgorithms.HmacSha256);
        var token = _keys.IssueToken(RsaJwtTestKeys.UserClaims(), credentials: hmac);

        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);

        act.Should().Throw<SecurityTokenException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/nonexistent/jwt-public.pem")]
    public void AddXAuth_UnusablePublicKey_FailsAtRegistration(string? publicKeyFile)
    {
        var act = () => new ServiceCollection().AddXAuth(Config(publicKeyFile!), ServiceId.Files);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{RsaJwtKeys.PublicKeyConfigKey}*");
    }

    [Fact]
    public async Task ServicePolicy_RejectsTokenIssuedForOtherService()
    {
        var services = BuildAuthorizedServices(ServiceId.Files);
        var authorization = services.GetRequiredService<IAuthorizationService>();

        // Токен выдан для Users, а проверяет его Files.
        var principal = PrincipalFor(
            RsaJwtTestKeys.ServiceClaims(ServiceId.Identity, ServiceId.Users, ServicePermissions.RevocationRead));

        var result = await authorization.AuthorizeAsync(principal, null, nameof(TokenType.Service));

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ServicePolicy_AcceptsTokenIssuedForSelf()
    {
        var services = BuildAuthorizedServices(ServiceId.Files);
        var authorization = services.GetRequiredService<IAuthorizationService>();

        var principal = PrincipalFor(
            RsaJwtTestKeys.ServiceClaims(ServiceId.Identity, ServiceId.Files, ServicePermissions.RevocationRead));

        var result = await authorization.AuthorizeAsync(principal, null, nameof(TokenType.Service));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task UserPolicy_RejectsServiceToken()
    {
        var services = BuildAuthorizedServices(ServiceId.Files);
        var authorization = services.GetRequiredService<IAuthorizationService>();

        var principal = PrincipalFor(
            RsaJwtTestKeys.ServiceClaims(ServiceId.Web, ServiceId.Files, ServicePermissions.FilesServerRead));

        var result = await authorization.AuthorizeAsync(principal, null, nameof(TokenType.User));

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task UserPolicy_AcceptsUserToken()
    {
        var services = BuildAuthorizedServices(ServiceId.Files);
        var authorization = services.GetRequiredService<IAuthorizationService>();

        var principal = PrincipalFor(RsaJwtTestKeys.UserClaims());

        var result = await authorization.AuthorizeAsync(principal, null, nameof(TokenType.User));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task PermissionPolicy_RejectsTokenWithoutPermission()
    {
        var services = BuildAuthorizedServices(ServiceId.Users);
        var authorization = services.GetRequiredService<IAuthorizationService>();

        // Torrent имеет только revocation.read — права на users-server.write нет.
        var principal = PrincipalFor(
            RsaJwtTestKeys.ServiceClaims(ServiceId.Torrent, ServiceId.Users, ServicePermissions.RevocationRead));

        var result = await authorization.AuthorizeAsync(principal, null, XAuthPolicies.UsersServerWritePolicy);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task PermissionPolicy_AcceptsTokenWithPermission()
    {
        var services = BuildAuthorizedServices(ServiceId.Users);
        var authorization = services.GetRequiredService<IAuthorizationService>();

        var permissions = string.Join(' ', ServicePermissions.For(ServiceId.Identity));
        var principal = PrincipalFor(
            RsaJwtTestKeys.ServiceClaims(ServiceId.Identity, ServiceId.Users, permissions));

        var result = await authorization.AuthorizeAsync(principal, null, XAuthPolicies.UsersServerWritePolicy);

        result.Succeeded.Should().BeTrue();
    }

    private ServiceProvider BuildAuthorizedServices(ServiceId serviceId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXAuth(Config(_keys.PublicKeyFile), serviceId);
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal PrincipalFor(IEnumerable<Claim> claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    public void Dispose() => _keys.Dispose();
}
