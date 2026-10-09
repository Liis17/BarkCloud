using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Endpoints;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace BarkCloud.Web.Tests.Auth;

public sealed class LongUploadFactAttribute : FactAttribute
{
    public LongUploadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BARKCLOUD_LONG_UPLOAD_TEST") != "1")
            Skip = "Настоящая upstream-передача дольше 100 с: BARKCLOUD_LONG_UPLOAD_TEST=1.";
    }
}

/// <summary>Legacy <c>POST /api/files/upload</c>: срок upstream-передачи и отмена браузером (F17).</summary>
public sealed class LegacyUploadEndpointsTests
{
    private static readonly byte[] Payload = "legacy upload bytes"u8.ToArray();

    [Fact]
    public async Task Success_StreamsMultipartToFilesHttp1_AndReturnsFileId()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Respond = async (request, ct) =>
        {
            request.RequestUri!.ToString().Should().Be($"{Fixture.Http1Base}/upload/{fixture.FileId}");
            var form = await ReadFormAsync(request, ct);
            form.Should().Be(("file", "photo.jpg", Convert.ToBase64String(Payload)));
            return Json(HttpStatusCode.OK, $$"""{"fileId":"{{fixture.FileId}}"}""");
        };

        using var response = await fixture.UploadAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("fileId").GetString().Should().Be(fixture.FileId);
        json.RootElement.GetProperty("name").GetString().Should().Be("photo.jpg");
        fixture.Handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task FilesError_KeepsStatusAndBody()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Respond = (_, _) => Task.FromResult(Json(HttpStatusCode.Conflict, "quota exceeded"));

        using var response = await fixture.UploadAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Be("quota exceeded");
    }

    [Fact]
    public async Task UnparsableFilesResponse_Returns502()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Respond = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "not json"));

        using var response = await fixture.UploadAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task BrowserCancel_CancelsUpstreamTransfer_AndDisposesMultipart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? captured = null;
        fixture.Handler.Respond = async (request, ct) =>
        {
            captured = request;
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { upstreamCancelled.TrySetResult(); }
            throw new InvalidOperationException("unreachable");
        };

        using var browser = new CancellationTokenSource();
        var upload = fixture.UploadAsync(browser.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        browser.Cancel();

        await FluentActions.Awaiting(() => upload).Should().ThrowAsync<OperationCanceledException>();
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // using var content в обработчике освобождает multipart вместе с потоком файла формы.
        (await WaitDisposedAsync(captured!.Content!)).Should().BeTrue();
    }

    private static async Task<bool> WaitDisposedAsync(HttpContent content)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try { await content.ReadAsStreamAsync(); }
            catch (ObjectDisposedException) { return true; }
            catch (InvalidOperationException) { /* ещё не освобождён */ }
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public void TransferClient_HasTwoHourTimeout()
    {
        using var provider = new ServiceCollection().AddLegacyUploadTransferClient().BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        factory.CreateClient(LegacyUploadTransfer.ClientName).Timeout.Should().Be(TimeSpan.FromHours(2));
    }

    [LongUploadFact]
    public async Task RealMultipartTransfer_LongerThan100Seconds_Succeeds()
    {
        await using var files = await SlowFilesStub.StartAsync(TimeSpan.FromSeconds(105));
        using var provider = new ServiceCollection().AddLegacyUploadTransferClient().BuildServiceProvider();
        await using var fixture = await Fixture.CreateAsync(provider.GetRequiredService<IHttpClientFactory>(), files.BaseUrl);

        var started = DateTime.UtcNow;
        using var response = await fixture.UploadAsync();

        (DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromSeconds(100));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        files.ReceivedBytes.Should().Be(Payload.Length);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("fileId").GetString().Should().Be(fixture.FileId);
    }

    private static async Task<(string Name, string? FileName, string Base64)> ReadFormAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var content = request.Content.Should().BeOfType<MultipartFormDataContent>().Subject;
        var part = content.Single();
        var bytes = await part.ReadAsByteArrayAsync(ct);
        return (part.Headers.ContentDisposition!.Name!.Trim('"'), part.Headers.ContentDisposition.FileName?.Trim('"'), Convert.ToBase64String(bytes));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    /// <summary>Kestrel-заглушка Files <c>POST /upload/{id}</c>: читает multipart и отвечает с задержкой.</summary>
    private sealed class SlowFilesStub : IAsyncDisposable
    {
        private WebApplication App { get; init; } = null!;
        public string BaseUrl => App.Urls.Single();
        public long ReceivedBytes { get; private set; }

        public static async Task<SlowFilesStub> StartAsync(TimeSpan delay)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            var stub = new SlowFilesStub { App = builder.Build() };
            stub.App.MapPost("/upload/{id}", async (HttpContext http, string id) =>
            {
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                stub.ReceivedBytes = form.Files["file"]!.Length;
                await Task.Delay(delay, http.RequestAborted);
                return Results.Json(new { fileId = id });
            }).DisableAntiforgery();
            await stub.App.StartAsync();
            return stub;
        }

        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Http1Base = "http://files.internal:7026";
        private const string Secret = "legacy-upload-test-signing-key-at-least-32-bytes";
        private WebApplication App { get; set; } = null!;
        private HttpClient Client { get; set; } = null!;
        private string Jwt { get; } = CreateJwt();
        public string FileId { get; } = Guid.NewGuid().ToString();
        public Handler Handler { get; } = new();

        public static async Task<Fixture> CreateAsync(IHttpClientFactory? httpFactory = null, string? http1Base = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["JwtSettings:SecretKey"] = Secret, ["JwtSettings:Issuer"] = "bark", ["JwtSettings:Audience"] = "bark" }).Build();
            builder.Configuration["FilesService:Http1Base"] = http1Base ?? Http1Base;
            var fixture = new Fixture();

            builder.Services.AddSingleton(new AuthGateway(new Mock<IdentityApi.IdentityApiClient>(MockBehavior.Strict).Object,
                new TokenRevocationCache(), config, NullLogger<AuthGateway>.Instance));
            var files = new Mock<FilesApi.FilesApiClient>(MockBehavior.Strict);
            files.Setup(x => x.GetUploadUrlAsync(It.Is<GetUploadUrlRequest>(r => r.FileType == UploadFileType.CloudFile),
                    It.Is<Metadata>(m => m.Any(h => h.Value.Contains(fixture.Jwt))), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .Returns(() => GrpcCallHelpers.AsyncUnary(new GetUploadUrlResponse { FileId = fixture.FileId, Url = "https://public.example/upload/" + fixture.FileId }));
            builder.Services.AddSingleton(files.Object);
            // Остальные клиенты нужны только для сборки endpoints группы /api.
            builder.Services.AddSingleton(new Mock<CloudApi.CloudApiClient>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton(new Mock<AlbumApi.AlbumApiClient>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton(new Mock<MusicApi.MusicApiClient>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton(new Mock<DynamicFolderApi.DynamicFolderApiClient>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton(new Mock<SearchApi.SearchApiClient>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton(new Mock<FilesServerApi.FilesServerApiClient>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton(new Mock<UsersApi.UsersApiClient>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton(new Mock<UsersServerApi.UsersServerApiClient>(MockBehavior.Strict).Object);
            builder.Services.AddScoped<PageDataBuilder>();

            if (httpFactory is null)
            {
                var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
                factory.Setup(x => x.CreateClient(LegacyUploadTransfer.ClientName))
                    .Returns(() => new HttpClient(fixture.Handler, disposeHandler: false));
                httpFactory = factory.Object;
            }
            builder.Services.AddSingleton(httpFactory);

            fixture.App = builder.Build(); fixture.App.MapCloudApiEndpoints();
            await fixture.App.StartAsync();
            fixture.Client = new HttpClient { BaseAddress = new Uri(fixture.App.Urls.Single()), Timeout = Timeout.InfiniteTimeSpan };
            fixture.Client.DefaultRequestHeaders.Add("Cookie", AuthGateway.AccessCookie + "=" + fixture.Jwt);
            return fixture;
        }

        public Task<HttpResponseMessage> UploadAsync(CancellationToken ct = default)
        {
            var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(Payload);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(part, "file", "photo.jpg");
            return Client.PostAsync("/api/files/upload", form, ct);
        }

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
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => throw new InvalidOperationException("Ответ Files не задан");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Respond(request, cancellationToken);
        }
    }
}
