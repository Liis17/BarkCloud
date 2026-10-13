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

public sealed class StorageQuotaGroupEndpointsTests
{
    [Fact]
    public async Task SaveAndDelete_RequireSessionAndAdminGateBeforeCallingConfiguration()
    {
        const string secret = "storage-group-test-signing-key-at-least-32-bytes";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:SecretKey"] = secret,
            ["JwtSettings:Issuer"] = "bark",
            ["JwtSettings:Audience"] = "bark",
            ["App:AdminPassword"] = "admin-pass"
        }).Build();
        var auth = new AuthGateway(new Mock<IdentityApi.IdentityApiClient>(MockBehavior.Strict).Object,
            new TokenRevocationCache(), config, NullLogger<AuthGateway>.Instance);
        var admin = new AdminGate(config);
        var configurationClient = new Mock<ConfigurationApi.ConfigurationApiClient>(MockBehavior.Strict);
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton(admin);
        builder.Services.AddSingleton(new ConfigurationManagementGateway(configurationClient.Object, config, new S3AccessChecker()));
        await using var app = builder.Build();
        SettingsEndpoints.MapStorageQuotaGroupEndpoints(app.MapGroup("/api/settings"));
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(IdentityClaims.UserId, "42"),
                new Claim(IdentityClaims.TokenType, TokenType.User.ToString())
            ]),
            Issuer = "bark",
            Audience = "bark",
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new(new SymmetricSecurityKey(JwtSecret.GetKeyBytes(secret)), SecurityAlgorithms.HmacSha256)
        };
        var jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
        var unlocked = new DefaultHttpContext();
        admin.Unlock(unlocked, "admin-pass").Should().BeTrue();
        var adminCookie = unlocked.Response.Headers.SetCookie.ToString().Split(';')[0];
        var endpoints = new (string Path, object Body)[]
        {
            ("/api/settings/server/storage/groups/save", new
            {
                id = (string?)null,
                name = "Фото",
                quotaValue = "2",
                quotaUnit = "tb",
                buckets = new[] { new { serviceUrl = "https://s3.example", bucketName = "photos" } }
            }),
            ("/api/settings/server/storage/groups/delete", new { id = "grp-photos" })
        };

        foreach (var endpoint in endpoints)
        {
            using var anonymous = new HttpRequestMessage(HttpMethod.Post, endpoint.Path) { Content = JsonContent.Create(endpoint.Body) };
            using var anonymousResponse = await client.SendAsync(anonymous);
            anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, endpoint.Path);

            using var locked = new HttpRequestMessage(HttpMethod.Post, endpoint.Path) { Content = JsonContent.Create(endpoint.Body) };
            locked.Headers.Add("Cookie", AuthGateway.AccessCookie + "=" + jwt);
            using var lockedResponse = await client.SendAsync(locked);
            lockedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, endpoint.Path);
        }

        configurationClient.VerifyNoOtherCalls();
    }
}
