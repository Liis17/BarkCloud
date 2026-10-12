using System.Net;
using System.Security.Cryptography;

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
using Microsoft.Extensions.Time.Testing;

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
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly Mock<FilesApi.FilesApiClient> _files = new(MockBehavior.Strict);
    private readonly Mock<CloudApi.CloudApiClient> _cloud = new(MockBehavior.Strict);
    private readonly Mock<ITorrentManagerFile> _torrentFile = new();
    private readonly Handler _handler = new();

    public TorrentImportServiceTests()
    {
        _path = Path.Combine(_dir, FileName);
        File.WriteAllBytes(_path, "torrent bytes"u8.ToArray());
        _torrentFile.SetupGet(x => x.FullPath).Returns(_path);
        _files.Setup(x => x.CreateUploadSessionAsync(It.Is<CreateUploadSessionRequest>(r =>
                r.FileName == FileName && r.FileSize == new FileInfo(_path).Length
                && r.ContentType == "application/octet-stream" && r.IdempotencyKey.Length == 32
                && r.Sha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_path)))),
                It.Is<Metadata>(m => m.GetValue("x-auth-token") == "user-jwt"), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(Session(UploadSessionStatus.Uploading)));
        _files.Setup(x => x.CompleteUploadSessionAsync(It.Is<UploadSessionIdRequest>(r => r.SessionId == _sessionId),
                It.Is<Metadata>(m => m.GetValue("x-auth-token") == "user-jwt"), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(Session(UploadSessionStatus.Ready)));
        _files.Setup(x => x.CancelUploadSessionAsync(It.Is<UploadSessionIdRequest>(r => r.SessionId == _sessionId),
                It.Is<Metadata>(m => m.GetValue("x-auth-token") == "user-jwt"), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(Session(UploadSessionStatus.Cancelled)));
    }

    private UploadSessionResponse Session(UploadSessionStatus status) => new()
    {
        SessionId = _sessionId, FileId = _fileId, Status = status,
        PartSize = 5, UploadToken = "part-token", FileSize = new FileInfo(_path).Length
    };

    [Theory]
    [InlineData(DirectoryId, false)]
    [InlineData("", true)]
    public async Task Success_UploadsPartsAndAttachesWithSessionId(string directoryId, bool routeByMediaKind)
    {
        var received = new List<byte>();
        var number = 0;
        _handler.Respond = async (request, ct) =>
        {
            number++;
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.AbsolutePath.Should().Be($"/file-upload/{_sessionId}/parts/{number}");
            request.RequestUri.Host.Should().Be("127.0.0.1");
            request.RequestUri.Port.Should().Be(int.TryParse(Environment.GetEnvironmentVariable("FILES_HTTP1PORT"), out var port) && port > 0 ? port : 7026);
            request.Headers.GetValues("X-Upload-Token").Should().Equal("part-token");
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var start = received.Count;
            bytes.Length.Should().Be(Math.Min(5, 13 - start));
            request.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            request.Content.Headers.ContentLength.Should().Be(bytes.Length);
            request.Content.Headers.ContentRange!.ToString().Should().Be($"bytes {start}-{start + bytes.Length - 1}/13");
            received.AddRange(bytes);
            return Receipt(number, bytes.Length);
        };
        _cloud.Setup(x => x.AttachFileAsync(
                It.Is<AttachFileRequest>(r => r.DirectoryId == directoryId && r.FileId == _fileId && r.Name == FileName
                    && r.RouteByMediaKind == routeByMediaKind && r.UploadSessionId == _sessionId && !r.IsUploadRetry),
                It.Is<Metadata>(m => m.GetValue("x-auth-token") == "user-jwt"), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new CloudEmpty()));

        var imported = await CreateService(MockFactory()).ImportFileAsync(_torrentFile.Object, directoryId, "user-jwt", CancellationToken.None);

        imported.Should().Be(new TorrentImportService.ImportedFile(_fileId, FileName));
        received.Should().Equal("torrent bytes"u8.ToArray());
        number.Should().Be(3);
        _cloud.VerifyAll();
        VerifyCancel(Times.Never());
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
        VerifyCancel(Times.Once());
        var cancelCall = _files.Invocations.Single(x => x.Method.Name == nameof(FilesApi.FilesApiClient.CancelUploadSessionAsync));
        ((CancellationToken)cancelCall.Arguments[3]).IsCancellationRequested.Should().BeFalse();
        ((DateTime?)cancelCall.Arguments[2]).Should().NotBeNull();
        _cloud.VerifyNoOtherCalls();
        VerifyCancel(Times.Once());
        AssertFileReleased();
    }

    [Fact]
    public async Task RpcCancelledByImportTimeout_ThrowsTimeoutException()
    {
        _handler.Respond = async (request, ct) => Receipt(
            int.Parse(request.RequestUri!.Segments.Last()), (await request.Content!.ReadAsByteArrayAsync(ct)).Length);
        var grpcStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _files.Setup(x => x.CompleteUploadSessionAsync(It.IsAny<UploadSessionIdRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((UploadSessionIdRequest _, Metadata _, DateTime? _, CancellationToken callToken) =>
            {
                grpcStarted.SetResult();
                var response = new TaskCompletionSource<UploadSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                callToken.Register(() => response.TrySetException(new RpcException(new Status(StatusCode.Cancelled, "timeout"))));
                return new AsyncUnaryCall<UploadSessionResponse>(response.Task, Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess, () => new Metadata(), () => { });
            });
        var timeProvider = new FakeTimeProvider();

        var import = CreateService(MockFactory(), timeProvider)
            .ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None);
        await grpcStarted.Task;
        timeProvider.Advance(TorrentImportService.TransferTimeout);

        var exception = await FluentActions.Awaiting(() => import).Should().ThrowAsync<TimeoutException>();
        exception.Which.InnerException.Should().BeOfType<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.Cancelled);
        VerifyCancel(Times.Once());
        _cloud.VerifyNoOtherCalls();
        AssertFileReleased();
    }

    [Fact]
    public async Task Processing_PollsUntilReadyBeforeAttach()
    {
        _handler.Respond = async (request, ct) => Receipt(
            int.Parse(request.RequestUri!.Segments.Last()), (await request.Content!.ReadAsByteArrayAsync(ct)).Length);
        _files.Setup(x => x.CompleteUploadSessionAsync(It.IsAny<UploadSessionIdRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(Session(UploadSessionStatus.Processing)));
        var polls = 0;
        _files.Setup(x => x.GetUploadSessionAsync(It.Is<UploadSessionIdRequest>(r => r.SessionId == _sessionId),
                It.Is<Metadata>(m => m.GetValue("x-auth-token") == "user-jwt"), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => GrpcCallHelpers.AsyncUnary(Session(++polls == 1 ? UploadSessionStatus.Processing : UploadSessionStatus.Ready)));
        _cloud.Setup(x => x.AttachFileAsync(It.Is<AttachFileRequest>(r => r.UploadSessionId == _sessionId),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback(() => polls.Should().Be(2))
            .Returns(GrpcCallHelpers.AsyncUnary(new CloudEmpty()));

        var result = await CreateService(MockFactory()).ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None);

        result!.FileId.Should().Be(_fileId);
        _cloud.VerifyAll();
        VerifyCancel(Times.Never());
    }

    [Fact]
    public async Task FailedProcessing_CancelsAndReportsServerError()
    {
        _handler.Respond = async (request, ct) => Receipt(
            int.Parse(request.RequestUri!.Segments.Last()), (await request.Content!.ReadAsByteArrayAsync(ct)).Length);
        var failed = Session(UploadSessionStatus.Failed);
        failed.ErrorCode = "integrity_mismatch";
        failed.ErrorMessage = "Хеш не совпадает";
        _files.Setup(x => x.CompleteUploadSessionAsync(It.IsAny<UploadSessionIdRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(failed));

        await FluentActions.Awaiting(() => CreateService(MockFactory()).ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*integrity_mismatch*Хеш не совпадает*");

        VerifyCancel(Times.Once());
        _cloud.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvalidReceipt_DoesNotCompleteOrAttach()
    {
        _handler.Respond = (_, _) => Task.FromResult(Receipt(1, 999));
        await FluentActions.Awaiting(() => CreateService(MockFactory()).ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
        _files.Verify(x => x.CompleteUploadSessionAsync(It.IsAny<UploadSessionIdRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never());
        VerifyCancel(Times.Once());
        _cloud.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EmptyFile_FailsBeforeCreatingSession()
    {
        File.WriteAllBytes(_path, []);
        await FluentActions.Awaiting(() => CreateService(MockFactory()).ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*пустой файл*");
        _files.VerifyNoOtherCalls();
        _cloud.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelFailure_PreservesOriginalUploadError()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        _files.Setup(x => x.CancelUploadSessionAsync(It.IsAny<UploadSessionIdRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "Files unavailable")));
        await FluentActions.Awaiting(() => CreateService(MockFactory()).ImportFileAsync(_torrentFile.Object, DirectoryId, "user-jwt", CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();
        VerifyCancel(Times.Once());
    }

    private void VerifyCancel(Times times) => _files.Verify(x => x.CancelUploadSessionAsync(
        It.Is<UploadSessionIdRequest>(r => r.SessionId == _sessionId), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), times);

    private static HttpResponseMessage Receipt(int partNumber, long size) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"partNumber\":{partNumber},\"size\":{size}}}", System.Text.Encoding.UTF8, "application/json")
    };

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
    public async Task RealPartTransfer_LongerThan100Seconds_Succeeds()
    {
        long received = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        await using var stub = builder.Build();
        stub.MapPut("/file-upload/{id}/parts/{partNumber:int}", async (HttpContext http, int partNumber) =>
        {
            await http.Request.Body.CopyToAsync(Stream.Null, http.RequestAborted);
            received += http.Request.ContentLength!.Value;
            if (partNumber == 1)
                await Task.Delay(TimeSpan.FromSeconds(105), http.RequestAborted);
            return Results.Json(new { partNumber, size = http.Request.ContentLength.Value });
        });
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

    private TorrentImportService CreateService(IHttpClientFactory factory, TimeProvider? timeProvider = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FilesService:Host"] = "http://127.0.0.1:7024" })
            .Build();
        return new TorrentImportService(_files.Object, _cloud.Object, factory, config, NullLogger<TorrentImportService>.Instance, timeProvider);
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
