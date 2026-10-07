using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;

using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Endpoints;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace BarkCloud.Web.Tests.Auth;

public sealed class TextPreviewEndpointsTests
{
    [Fact]
    public async Task PrivateRoutes_RequireSession_AndForwardUserToken()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid().ToString();
        foreach (var path in new[] { "/api/files/text?id=" + id, "/api/shared/text?fileId=" + id })
            (await fixture.Client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        fixture.Files.Setup(x => x.GetTempDownloadUrlAsync(It.Is<GetTempDownloadUrlRequest>(r => r.FileIds.Single() == id),
            It.Is<Metadata>(m => m.Any(h => h.Value.Contains(fixture.Jwt))), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetTempDownloadUrlResponse { FileUrls = { new GetTempDownloadUrlResponse.Types.DownloadFileData { FileId = id, Url = fixture.DownloadUrl } } }));
        fixture.Cloud.Setup(x => x.GetSharedFileDownloadUrlAsync(It.Is<GetSharedFileDownloadUrlRequest>(r => r.FileId == id),
            It.Is<Metadata>(m => m.Any(h => h.Value.Contains(fixture.Jwt))), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetSharedFileDownloadUrlResponse { DownloadUrl = fixture.DownloadUrl }));
        fixture.Authenticate();
        foreach (var path in new[] { "/api/files/text?id=" + id, "/api/shared/text?fileId=" + id })
        {
            using var response = await fixture.Client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("text");
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        fixture.Files.VerifyAll(); fixture.Cloud.VerifyAll();
    }

    [Fact]
    public async Task SharedRoute_MapsRevokedGrantToForbidden_WithoutFetchingBytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var trailers = new Metadata { { "x-error-code", "D4F1E2A8-9C5B-4A77-83BB-5E6F7A8B9C01" } };
        fixture.Cloud.Setup(x => x.GetSharedFileDownloadUrlAsync(It.IsAny<GetSharedFileDownloadUrlRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.FailedPrecondition, "denied"), trailers));
        fixture.Authenticate();
        (await fixture.Client.GetAsync("/api/shared/text?fileId=" + Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fixture.Handler.Requests.Should().Be(0);
    }

    [Fact]
    public async Task PublicFile_ResolvesTokenWithoutUserSession_AndRejectsRevokedLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Setup(x => x.ResolveShareAsync(It.Is<ResolveShareRequest>(r => r.Token == "live"), null, null, It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new ResolveShareResponse { Found = true, DownloadUrl = fixture.DownloadUrl }));
        fixture.Server.Setup(x => x.ResolveShareAsync(It.Is<ResolveShareRequest>(r => r.Token == "revoked"), null, null, It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new ResolveShareResponse { Found = false }));
        (await fixture.Client.GetAsync("/api/public/shares/live/text")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await fixture.Client.GetAsync("/api/public/shares/revoked/text")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        fixture.Handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task PublicFolder_ChecksMembershipInResolvedDirectory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid().ToString();
        fixture.Server.Setup(x => x.ResolveFolderShareAsync(It.Is<ResolveFolderShareRequest>(r => r.Token == "folder" && r.Dir == "child"), null, null, It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(new ResolveFolderShareResponse { Found = true,
                Files = { new PublicFileEntry { FileId = id, DownloadUrl = fixture.DownloadUrl } } }));
        (await fixture.Client.GetAsync($"/api/public/folder-shares/folder/text?dir=child&fileId={id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await fixture.Client.GetAsync($"/api/public/folder-shares/folder/text?dir=child&fileId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        fixture.Handler.Requests.Should().Be(1);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Secret = "text-preview-test-signing-key-at-least-32-bytes";
        private WebApplication App { get; set; } = null!;
        public HttpClient Client { get; set; } = null!;
        public string Jwt { get; init; } = "";
        public string DownloadUrl { get; } = "https://files.example/download/" + Guid.NewGuid();
        public Mock<FilesApi.FilesApiClient> Files { get; } = new(MockBehavior.Strict);
        public Mock<CloudApi.CloudApiClient> Cloud { get; } = new(MockBehavior.Strict);
        public Mock<FilesServerApi.FilesServerApiClient> Server { get; } = new(MockBehavior.Strict);
        public Handler Handler { get; } = new();

        public static async Task<Fixture> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["JwtSettings:SecretKey"] = Secret, ["JwtSettings:Issuer"] = "bark", ["JwtSettings:Audience"] = "bark" }).Build();
            var fixture = new Fixture { Jwt = CreateJwt() };
            builder.Services.AddSingleton(new AuthGateway(new Mock<IdentityApi.IdentityApiClient>(MockBehavior.Strict).Object,
                new TokenRevocationCache(), config, NullLogger<AuthGateway>.Instance));
            builder.Services.AddSingleton(fixture.Files.Object); builder.Services.AddSingleton(fixture.Cloud.Object); builder.Services.AddSingleton(fixture.Server.Object);
            var factory = new Mock<IHttpClientFactory>(); factory.Setup(x => x.CreateClient("files-upload")).Returns(() => new HttpClient(fixture.Handler, disposeHandler: false));
            builder.Services.AddSingleton(new TextPreviewProxy(factory.Object, config));
            fixture.App = builder.Build(); fixture.App.MapTextPreviewEndpoints();
            await fixture.App.StartAsync(); fixture.Client = new HttpClient { BaseAddress = new Uri(fixture.App.Urls.Single()) };
            return fixture;
        }

        public void Authenticate() => Client.DefaultRequestHeaders.Add("Cookie", AuthGateway.AccessCookie + "=" + Jwt);
        private static string CreateJwt()
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([new Claim(IdentityClaims.UserId, "42"), new Claim(IdentityClaims.TokenType, TokenType.User.ToString())]),
                Issuer = "bark", Audience = "bark", IssuedAt = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new(new SymmetricSecurityKey(JwtSecret.GetKeyBytes(Secret)), SecurityAlgorithms.HmacSha256)
            }));
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); Handler.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("text") });
        }
    }
}
