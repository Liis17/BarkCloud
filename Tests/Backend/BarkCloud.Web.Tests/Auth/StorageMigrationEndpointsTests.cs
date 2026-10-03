using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Configuration;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Web;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace BarkCloud.Web.Tests.Auth;

public sealed class StorageMigrationEndpointsTests
{
    [Fact]
    public async Task EveryEndpoint_RequiresUserSessionAndAdministrativeUnlock_AndSourcesNeverReturnCredentials()
    {
        const string secret = "migration-test-signing-key-at-least-32-bytes";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["JwtSettings:SecretKey"] = secret, ["JwtSettings:Issuer"] = "bark", ["JwtSettings:Audience"] = "bark", ["App:AdminPassword"] = "admin-pass" }).Build();
        var auth = new AuthGateway(new Mock<IdentityApi.IdentityApiClient>(MockBehavior.Strict).Object,
            new TokenRevocationCache(), config, NullLogger<AuthGateway>.Instance);
        var admin = new AdminGate(config);
        var control = new Mock<IStorageMigrationControl>(MockBehavior.Strict);
        control.Setup(x => x.GetProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new StorageProfileItem
        { ProfileId = "images-v1", Role = "images", Version = 1, ServiceUrl = "https://source.example", BucketName = "files", AccessKey = "PRIVATE_ACCESS", SecretKey = "PRIVATE_SECRET" }]);
        var factory = new MigrationS3ClientFactory();
        builder.Services.AddSingleton(auth); builder.Services.AddSingleton(admin);
        builder.Services.AddSingleton(new StorageMigrationService(control.Object, factory, new S3MigrationCopier(factory), NullLogger<StorageMigrationService>.Instance));
        await using var app = builder.Build(); app.MapGroup("/api/settings").MapStorageMigrationEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(IdentityClaims.UserId, "42"), new Claim(IdentityClaims.TokenType, TokenType.User.ToString())]),
            Issuer = "bark", Audience = "bark", IssuedAt = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new(new SymmetricSecurityKey(JwtSecret.GetKeyBytes(secret)), SecurityAlgorithms.HmacSha256)
        };
        var handler = new JwtSecurityTokenHandler(); var jwt = handler.WriteToken(handler.CreateToken(descriptor));
        var unlocked = new DefaultHttpContext(); admin.Unlock(unlocked, "admin-pass").Should().BeTrue();
        var adminCookie = unlocked.Response.Headers.SetCookie.ToString().Split(';')[0];
        var endpoints = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/migration/sources"), (HttpMethod.Post, "/migration/check"), (HttpMethod.Post, "/migration/start"),
            (HttpMethod.Get, "/migration/jobs"), (HttpMethod.Get, "/migration/jobs/id"),
            (HttpMethod.Post, "/migration/jobs/id/retry"), (HttpMethod.Post, "/migration/jobs/id/cancel"),
            (HttpMethod.Post, "/server/storage/migration/apply"), (HttpMethod.Get, "/migration/cutovers"),
            (HttpMethod.Post, "/migration/cutovers/id/cancel"), (HttpMethod.Post, "/migration/cutovers/id/recover")
        };
        foreach (var endpoint in endpoints)
        {
            foreach (var authenticated in new[] { false, true })
            {
                using var request = new HttpRequestMessage(endpoint.Method, "/api/settings" + endpoint.Path);
                request.Headers.Add("Cookie", authenticated ? AuthGateway.AccessCookie + "=" + jwt : adminCookie);
                if (endpoint.Method == HttpMethod.Post) request.Content = JsonContent.Create(new
                { sourceId = "id", validationId = "id", jobId = "id", destination = new MigrationConnection("https://target.example", "access", "secret", "to") });
                using var response = await client.SendAsync(request);
                response.StatusCode.Should().Be(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, endpoint.Path);
            }
        }
        control.Verify(x => x.GetProfilesAsync(It.IsAny<CancellationToken>()), Times.Never);
        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/settings/migration/sources");
        allowed.Headers.Add("Cookie", AuthGateway.AccessCookie + "=" + jwt + "; " + adminCookie);
        using var sources = await client.SendAsync(allowed); sources.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await sources.Content.ReadAsStringAsync(); json.Should().Contain("images-v1").And.NotContain("PRIVATE_ACCESS").And.NotContain("PRIVATE_SECRET");
    }
}
