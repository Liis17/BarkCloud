using System.Net;

using BarkCloud.Proto.Files;
using BarkCloud.TestKit;
using BarkCloud.Torrent.Infrastructure;

using Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using MonoTorrent;

namespace BarkCloud.Torrent.Tests.Infrastructure;

public sealed class LongUploadFactAttribute : FactAttribute
{
    public LongUploadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BARKCLOUD_LONG_UPLOAD_TEST") != "1")
            Skip = "Настоящая upstream-передача дольше 100 с: BARKCLOUD_LONG_UPLOAD_TEST=1.";
    }
}

/// <summary>Импорт в облако: срок передачи в Files и отмена через ct (F17).</summary>
public sealed class TorrentImportServiceTests : IDisposable
{
    private const string DirectoryId = "dir-1";
    private const string FileName = "movie.mkv";

    private readonly string _dir = Directory.CreateTempSubdirectory("bark-import-").FullName;
    private readonly string _path;
    private readonly string _fileId = Guid.NewGuid().ToString();
    private readonly Mock<FilesApi.FilesApiClient> _files = new(MockBehavior.Strict);
    private readonly Mock<CloudApi.CloudApiClient> _cloud = new(MockBehavior.Strict);
    private readonly Mock<ITorrentManagerFile> _torrentFile = new();
    private readonly Handler _handler = new();

    public TorrentImportServiceTests()
    {
        _path = Path.Combine(_dir, FileName);
        File.WriteAllBytes(_path, "torrent bytes"u8.ToArray());
        _torrentFile.SetupGet(x => x.FullPath).Returns(_path);
        _files.Setup(x => x.GetUploadUrlAsync(It.Is<GetUploadUrlRequest>(r => r.FileType == UploadFileType.CloudFile),
                It.Is<Metadata>(m => m.GetValue("x-auth-token") == "user-jwt"), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(new GetUploadUrlResponse { FileId = _fileId }));
    }

    [Fact]
    public async Task Success_UploadsBytesAndAttachesWithSameFields()
    {
        _handler.Respond = async (request, ct) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be($"/upload/{_fileId}");
            var part = request.Content.Should().BeOfType<MultipartFormDataContent>().Subject.Single();
            part.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be(FileName);
            (await part.ReadAsStringAsync(ct)).Should().Be("torrent bytes");
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        _cloud.Setup(x => x.AttachFileAsync(
                It.Is<AttachFileRequest>(r => r.DirectoryId == DirectoryId && r.FileId == _fileId && r.Name == FileName && !r.RouteByMediaKind),
                It.Is<Metadata>(m => m.GetValue("x-auth-token") == "user-jwt"), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new CloudEmpty()));

        var imported = await CreateService(MockFactory()).ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None);

        imported.Should().Be(new TorrentImportService.ImportedFile(_fileId, FileName));
        _cloud.VerifyAll();
        AssertFileReleased();
    }

    [Fact]
    public async Task Cancel_StopsUpstreamTransfer_AndReleasesFile()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Respond = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        using var cts = new CancellationTokenSource();

        var import = CreateService(MockFactory()).ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await FluentActions.Awaiting(() => import).Should().ThrowAsync<OperationCanceledException>();
        import.IsCompleted.Should().BeTrue();
        _cloud.VerifyNoOtherCalls();
        AssertFileReleased();
    }

    [Fact]
    public async Task FilesError_ThrowsAndDoesNotAttach()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await FluentActions.Awaiting(() => CreateService(MockFactory())
                .ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();

        _cloud.VerifyNoOtherCalls();
        AssertFileReleased();
    }

    [Fact]
    public void FilesUploadClient_HasTwoHourTimeout()
    {
        using var provider = TorrentImportService.AddFilesUploadClient(new ServiceCollection()).BuildServiceProvider();

        provider.GetRequiredService<IHttpClientFactory>().CreateClient(TorrentImportService.HttpClientName)
            .Timeout.Should().Be(TimeSpan.FromHours(2));
    }

    [LongUploadFact]
    public async Task RealMultipartTransfer_LongerThan100Seconds_Succeeds()
    {
        long received = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        await using var stub = builder.Build();
        stub.MapPost("/upload/{id}", async (HttpContext http, string id) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            received = form.Files["file"]!.Length;
            await Task.Delay(TimeSpan.FromSeconds(105), http.RequestAborted);
            return Results.Json(new { fileId = id });
        }).DisableAntiforgery();
        await stub.StartAsync();
        _cloud.Setup(x => x.AttachFileAsync(It.IsAny<AttachFileRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new CloudEmpty()));

        // ResolveFilesHttp1Base берёт порт из FILES_HTTP1PORT.
        var previousPort = Environment.GetEnvironmentVariable("FILES_HTTP1PORT");
        Environment.SetEnvironmentVariable("FILES_HTTP1PORT", new Uri(stub.Urls.Single()).Port.ToString());
        try
        {
            using var provider = TorrentImportService.AddFilesUploadClient(new ServiceCollection()).BuildServiceProvider();
            var started = DateTime.UtcNow;

            var imported = await CreateService(provider.GetRequiredService<IHttpClientFactory>())
                .ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None);

            (DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromSeconds(100));
            imported!.FileId.Should().Be(_fileId);
            received.Should().Be(new FileInfo(_path).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FILES_HTTP1PORT", previousPort);
        }
    }

    private TorrentImportService CreateService(IHttpClientFactory factory)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FilesService:Host"] = "http://127.0.0.1:7024" })
            .Build();
        return new TorrentImportService(_files.Object, _cloud.Object, factory, config, NullLogger<TorrentImportService>.Instance);
    }

    private IHttpClientFactory MockFactory()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(x => x.CreateClient(TorrentImportService.HttpClientName))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));
        return factory.Object;
    }

    private void AssertFileReleased()
    {
        using var exclusive = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        exclusive.Length.Should().BeGreaterThan(0);
    }

    public void Dispose()
    {
        _handler.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => throw new InvalidOperationException("Ответ Files не задан");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Respond(request, cancellationToken);
    }
}
