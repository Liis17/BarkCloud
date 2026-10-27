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

namespace BarkCloud.Web.Tests.Endpoints;

public sealed class FileViewEndpointTests : IDisposable
{
    private readonly RsaJwtTestKeys _keys = new();

    [Fact]
    public async Task AuthenticatedViewDownloadsAsAttachmentAndSuppressesContentTypeSniffing()
    {
        var fileId = Guid.NewGuid().ToString();
        var downloadId = Guid.NewGuid();
        var fileClient = new Mock<FilesApi.FilesApiClient>(MockBehavior.Strict);
        fileClient.Setup(x => x.GetTempDownloadUrlAsync(
                It.Is<GetTempDownloadUrlRequest>(r => r.FileIds.Single() == fileId),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetTempDownloadUrlResponse
            {
                FileUrls = { new GetTempDownloadUrlResponse.Types.DownloadFileData
                {
                    FileId = fileId,
                    Url = "https://public.example/download/" + downloadId
                } }
            }));

        var body = new byte[] { 0x3c, 0x73, 0x63, 0x72, 0x69, 0x70, 0x74, 0x3e };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Encoding.UTF8.GetString(body), System.Text.Encoding.UTF8, "text/html")
        });
        using var upstreamClient = new HttpClient(handler, disposeHandler: false);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("files-upload")).Returns(upstreamClient);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RsaJwtKeys.PublicKeyConfigKey] = _keys.PublicKeyFile,
            ["JwtSettings:Issuer"] = "bark",
            ["JwtSettings:Audience"] = "bark",
            ["FilesService:Http1Base"] = "http://files-internal"
        }).Build();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IConfiguration>(config);
        builder.Services.AddSingleton(new AuthGateway(
            new Mock<IdentityApi.IdentityApiClient>(MockBehavior.Strict).Object,
            new TokenRevocationCache(), config, NullLogger<AuthGateway>.Instance));
        builder.Services.AddSingleton(fileClient.Object);
        builder.Services.AddSingleton(factory.Object);

        await using var app = builder.Build();
        app.MapFileViewEndpoint();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Cookie", AuthGateway.AccessCookie + "=" + CreateJwt());

        using var response = await client.GetAsync("/api/files/view?id=" + Uri.EscapeDataString(fileId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/octet-stream");
        response.Content.Headers.ContentDisposition!.ToString().Should().Be("attachment");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(body);
        handler.Url.Should().Be("http://files-internal/download/" + downloadId);
        fileClient.VerifyAll();

    }

    private string CreateJwt()
        => _keys.IssueToken([
            new Claim(IdentityClaims.UserId, "42"),
            new Claim(IdentityClaims.TokenType, TokenType.User.ToString())],
            expires: DateTime.UtcNow.AddHours(1),
            issuer: "bark",
            audience: "bark");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string? Url { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Url = request.RequestUri!.ToString();
            return Task.FromResult(responseFactory(request));
        }
    }

    public void Dispose() => _keys.Dispose();
}
