using BarkCloud.Identity.Host;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;

using Grpc.Core;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Host;

public class ServiceCredentialsApiServiceTests : IDisposable
{
    private const string UsersSecret = "users-service-credential";
    private const string TorrentSecret = "torrent-service-credential";

    private readonly RsaJwtTestKeys _keys = new();

    private ServiceCredentialsApiService BuildService(string? registry = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ServiceAuth:Credentials"] = registry ?? $"Users={UsersSecret}; Torrent={TorrentSecret}"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var jwtService = new JwtService(new JwtSettings
        {
            Issuer = RsaJwtTestKeys.TestIssuer,
            Audience = RsaJwtTestKeys.TestAudience,
            ExpiryMinutes = 60,
            PrivateKeyFile = _keys.PrivateKeyFile
        });
        return new ServiceCredentialsApiService(configuration, jwtService, NullLogger<ServiceCredentialsApiService>.Instance);
    }

    [Fact]
    public async Task GetServiceToken_ValidCredential_ReturnsTokenForTarget()
    {
        var sut = BuildService();

        var response = await sut.GetServiceToken(
            new GetServiceTokenRequest { Credential = UsersSecret, TargetService = (int)ServiceId.Files },
            new TestServerCallContext());

        response.Token.Should().NotBeNullOrEmpty();
        response.ExpiresAt.ToDateTime().Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task GetServiceToken_WrongCredential_IsUnauthenticated()
    {
        var sut = BuildService();

        var act = () => sut.GetServiceToken(
            new GetServiceTokenRequest { Credential = "totally-wrong-secret", TargetService = (int)ServiceId.Files },
            new TestServerCallContext());

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task GetServiceToken_EmptyCredential_IsUnauthenticated()
    {
        var sut = BuildService();

        var act = () => sut.GetServiceToken(
            new GetServiceTokenRequest { Credential = "", TargetService = (int)ServiceId.Files },
            new TestServerCallContext());

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task GetServiceToken_EmptyRegistry_FailsClosed()
    {
        var sut = BuildService(registry: "");

        var act = () => sut.GetServiceToken(
            new GetServiceTokenRequest { Credential = UsersSecret, TargetService = (int)ServiceId.Files },
            new TestServerCallContext());

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.Unauthenticated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task GetServiceToken_UnknownTarget_IsInvalidArgument(int targetService)
    {
        var sut = BuildService();

        var act = () => sut.GetServiceToken(
            new GetServiceTokenRequest { Credential = UsersSecret, TargetService = targetService },
            new TestServerCallContext());

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetServiceToken_IgnoresUnknownRegistryEntries_ServesValidOnes()
    {
        var sut = BuildService(registry: $"NotAService=abc;Users={UsersSecret};;  ;=empty");

        var response = await sut.GetServiceToken(
            new GetServiceTokenRequest { Credential = UsersSecret, TargetService = (int)ServiceId.Users },
            new TestServerCallContext());

        response.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetServiceToken_TorrentGetsOnlyRevocationPermission()
    {
        var sut = BuildService();

        var response = await sut.GetServiceToken(
            new GetServiceTokenRequest { Credential = TorrentSecret, TargetService = (int)ServiceId.Identity },
            new TestServerCallContext());

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(response.Token);
        jwt.Claims.Should().Contain(c =>
            c.Type == IdentityClaims.Permissions && c.Value == ServicePermissions.RevocationRead);
        jwt.Claims.Should().Contain(c =>
            c.Type == IdentityClaims.ServiceId && c.Value == ((int)ServiceId.Torrent).ToString());
        jwt.Claims.Should().Contain(c =>
            c.Type == IdentityClaims.TargetService && c.Value == ((int)ServiceId.Identity).ToString());
    }

    public void Dispose() => _keys.Dispose();
}
