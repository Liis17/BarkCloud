using System.Net;
using System.Net.Http.Headers;
using System.Text;

using BarkCloud.Web;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BarkCloud.Web.Tests.Endpoints;

public sealed class ImageProxyEndpointsTests
{
    private const string ContentSecurityPolicy = "default-src 'none'; sandbox; base-uri 'none'; form-action 'none'";
    private static readonly string[] Routes = ["/api/head/icon", "/api/files/image"];
    private static readonly string[] RasterTypes = ["image/jpeg", "image/png", "image/gif", "image/webp", "image/avif", "image/bmp"];

    [Theory]
    [MemberData(nameof(AllowedRasterTypes))]
    public async Task BothProxiesAllowOnlyRasterTypes_IgnoreCaseAndParameters(string route, string contentType)
    {
        var body = new byte[] { 0, 1, 2, 255 };
        await using var fixture = await Fixture.CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Content(body, contentType.ToUpperInvariant() + "; charset=binary")
        });

        using var response = await fixture.Client.GetAsync(ProxyPath(route, "https://files.example/download/" + Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(body);
        response.Content.Headers.ContentType!.MediaType.Should().BeEquivalentTo(contentType);
        AssertSafeHeaders(response);
    }

    public static IEnumerable<object[]> AllowedRasterTypes() =>
        Routes.SelectMany(route => RasterTypes.Select(type => new object[] { route, type }));

    [Theory]
    [InlineData("/api/head/icon", "image/svg+xml", "<svg onload=\"alert(1)\"><script>alert(1)</script></svg>")]
    [InlineData("/api/files/image", "image/svg+xml", "<svg onload=\"alert(1)\"><script>alert(1)</script></svg>")]
    [InlineData("/api/head/icon", "text/html", "<script>alert(1)</script>")]
    [InlineData("/api/files/image", "text/html", "<script>alert(1)</script>")]
    [InlineData("/api/head/icon", "application/xml", "<root/>")]
    [InlineData("/api/files/image", "application/xml", "<root/>")]
    [InlineData("/api/head/icon", "application/x-unknown", "body")]
    [InlineData("/api/files/image", "application/x-unknown", "body")]
    [InlineData("/api/head/icon", null, "body")]
    [InlineData("/api/files/image", null, "body")]
    public async Task RejectsNonRasterMimeBeforeReadingUpstreamBody(string route, string? contentType, string body)
    {
        CountingReadStream? source = null;
        await using var fixture = await Fixture.CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Content(body, contentType, stream => source = stream)
        });

        using var response = await fixture.Client.GetAsync(ProxyPath(route, "https://files.example/download/" + Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        source!.BytesRead.Should().Be(0);
        AssertSafeHeaders(response);
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task InvalidUrlsReturnBadRequestWithSafeHeaders(string route, string url)
    {
        await using var fixture = await Fixture.CreateAsync(_ => throw new InvalidOperationException("Upstream must not be called"));

        using var response = await fixture.Client.GetAsync(ProxyPath(route, url));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSafeHeaders(response);
    }

    public static IEnumerable<object[]> InvalidInputs() =>
        Routes.SelectMany(route => new[]
        {
            "not-a-url",
            "ftp://files.example/download/" + Guid.NewGuid(),
            "https://files.example/files/" + Guid.NewGuid(),
            "https://files.example/download/abc"
        }.Select(url => new object[] { route, url }));

    [Theory]
    [InlineData("/api/head/icon")]
    [InlineData("/api/files/image")]
    public async Task BothProxiesAlwaysFetchTheGuidFromTheInternalFilesHost(string route)
    {
        var id = Guid.NewGuid();
        Uri? requestUri = null;
        await using var fixture = await Fixture.CreateAsync(request =>
        {
            requestUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Content(new byte[] { 1, 2, 3 }, "image/png") };
        });

        using var response = await fixture.Client.GetAsync(
            ProxyPath(route, $"https://attacker.example/download/{id}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        requestUri.Should().Be(new Uri($"http://files-internal/download/{id}"));
        AssertSafeHeaders(response);
    }

    [Theory]
    [InlineData("/api/head/icon")]
    [InlineData("/api/files/image")]
    public async Task EmptyFilesBaseReturnsServiceUnavailableWithoutCallingUpstream(string route)
    {
        await using var fixture = await Fixture.CreateAsync(
            _ => throw new InvalidOperationException("Upstream must not be called"),
            filesServiceHttp1Base: "");

        using var response = await fixture.Client.GetAsync(
            ProxyPath(route, $"https://attacker.example/download/{Guid.NewGuid()}"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        AssertSafeHeaders(response);
    }

    [Theory]
    [InlineData("/api/head/icon")]
    [InlineData("/api/files/image")]
    public async Task UpstreamErrorResponsesCarrySafeHeaders(string route)
    {
        await using var upstreamErrorFixture = await Fixture.CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var upstreamError = await upstreamErrorFixture.Client.GetAsync(
            ProxyPath(route, "https://other.example/download/" + Guid.NewGuid()));
        upstreamError.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        AssertSafeHeaders(upstreamError);
    }

    [Theory]
    [InlineData(2_097_152, HttpStatusCode.OK)]
    [InlineData(2_097_153, HttpStatusCode.RequestEntityTooLarge)]
    public async Task IconLimitChecksActualBytesWhileStreaming(int length, HttpStatusCode expectedStatus)
    {
        CountingReadStream? source = null;
        await using var fixture = await Fixture.CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Content(new byte[length], "image/png", stream => source = stream)
        });

        using var response = await fixture.Client.GetAsync(
            ProxyPath("/api/head/icon", "https://files.example/download/" + Guid.NewGuid()));

        response.StatusCode.Should().Be(expectedStatus);
        source!.BytesRead.Should().BeLessThanOrEqualTo(2_097_153);
        AssertSafeHeaders(response);
    }

    [Fact]
    public async Task ImageProxyStreamsBodyAndPreservesBytes()
    {
        var bytes = Enumerable.Range(0, 250_000).Select(i => (byte)(i % 251)).ToArray();
        CountingReadStream? source = null;
        await using var fixture = await Fixture.CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Content(bytes, "image/png", stream => source = stream)
        });

        using var response = await fixture.Client.GetAsync(
            ProxyPath("/api/files/image", "https://files.example/download/" + Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        source!.BytesRead.Should().Be(bytes.Length);
        AssertSafeHeaders(response);
    }

    private static HttpContent Content(byte[] bytes, string? contentType, Action<CountingReadStream>? capture = null) =>
        Content(new CountingReadStream(bytes), contentType, capture);

    private static HttpContent Content(string body, string? contentType, Action<CountingReadStream>? capture = null) =>
        Content(new CountingReadStream(Encoding.UTF8.GetBytes(body)), contentType, capture);

    private static HttpContent Content(CountingReadStream stream, string? contentType, Action<CountingReadStream>? capture = null)
    {
        capture?.Invoke(stream);
        var content = new StreamContent(stream);
        if (contentType is not null)
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    private static string ProxyPath(string route, string upstreamUrl) =>
        route + "?url=" + Uri.EscapeDataString(upstreamUrl);

    private static void AssertSafeHeaders(HttpResponseMessage response)
    {
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Be(ContentSecurityPolicy);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _upstreamClient;

        private Fixture(WebApplication app, HttpClient client, HttpClient upstreamClient)
        {
            _app = app;
            Client = client;
            _upstreamClient = upstreamClient;
        }

        public HttpClient Client { get; }

        public static async Task<Fixture> CreateAsync(
            Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
            string? filesServiceHttp1Base = "http://files-internal")
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FilesService:Http1Base"] = filesServiceHttp1Base
            });
            var handler = new StubHandler(responseFactory);
            var httpFactory = new Mock<IHttpClientFactory>();
            var upstreamClient = new HttpClient(handler, disposeHandler: false);
            httpFactory.Setup(f => f.CreateClient("files-upload")).Returns(upstreamClient);
            builder.Services.AddSingleton(httpFactory.Object);

            var app = builder.Build();
            app.MapImageProxyEndpoints();
            await app.StartAsync();
            return new Fixture(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }, upstreamClient);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            _upstreamClient.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class CountingReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
