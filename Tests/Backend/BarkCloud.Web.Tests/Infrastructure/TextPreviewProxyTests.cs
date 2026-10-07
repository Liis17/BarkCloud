using System.Net;

using BarkCloud.Web.Infrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class TextPreviewProxyTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(10485760, true)]
    [InlineData(10485761, false)]
    public async Task LimitsActualBytes_EvenWithoutContentLength(int size, bool allowed)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[size]))
        };
        var proxy = CreateProxy(response);
        var context = new DefaultHttpContext();

        var result = await proxy.FetchAsync(context, "https://files.example/download/" + Guid.NewGuid());

        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        if (allowed)
            ((FileContentHttpResult)result).FileContents.Length.Should().Be(size);
        else
            ((IStatusCodeHttpResult)result).StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task RejectsDeclaredOversize_WithoutReadingBody()
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentLength = TextPreviewProxy.MaxBytes + 1;
        var result = await CreateProxy(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
            .FetchAsync(new DefaultHttpContext(), "https://files.example/download/" + Guid.NewGuid());
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task UsesInternalFilesAddress_AndPreservesBytes()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xfe, 65, 0]) });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("files-upload")).Returns(new HttpClient(handler));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["FilesService:Http1Base"] = "http://files:7026/" }).Build();

        var result = (FileContentHttpResult)await new TextPreviewProxy(factory.Object, config)
            .FetchAsync(new DefaultHttpContext(), "https://public.example/download/" + id);

        handler.Url.Should().Be("http://files:7026/download/" + id);
        result.FileContents.ToArray().Should().Equal(0xff, 0xfe, 65, 0);
        result.ContentType.Should().Be("application/octet-stream");
        result.FileDownloadName.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task RejectsInvalidDownloadUrl_AndHonorsCancellation()
    {
        var proxy = CreateProxy(new HttpResponseMessage(HttpStatusCode.OK));
        ((IStatusCodeHttpResult)await proxy.FetchAsync(new DefaultHttpContext(), "https://example.com/other")).StatusCode.Should().Be(502);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var action = () => proxy.FetchAsync(context, "https://files.example/download/" + Guid.NewGuid());
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CancelsReadingBody_WhenBrowserDisconnects()
    {
        var stream = new PendingStream();
        var proxy = CreateProxy(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var pending = proxy.FetchAsync(context, "https://files.example/download/" + Guid.NewGuid());
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var action = () => pending;
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static TextPreviewProxy CreateProxy(HttpResponseMessage response)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("files-upload")).Returns(new HttpClient(new Handler(response)));
        return new TextPreviewProxy(factory.Object, new ConfigurationBuilder().Build());
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Url = request.RequestUri!.ToString();
            return Task.FromResult(response);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class PendingStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
