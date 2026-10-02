using BarkCloud.Files.Domain;
using BarkCloud.Files.Host;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Metrics;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Host;

public class LegacyUploadAdmissionFilterTests : IDisposable
{
    private const string Multipart = "multipart/form-data; boundary=bark";

    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IStorageQuotaService> _quota = new();
    private readonly LegacyUploadOptions _options = new()
    {
        MaxFileBytes = 1000,
        MaxBufferedBytes = 5000,
        MultipartOverheadBytes = 100,
        QueueTimeout = TimeSpan.FromMilliseconds(50)
    };
    private readonly MetricsCollector _metrics = new();
    private LegacyUploadBudget _budget = null!;

    private sealed class Harness
    {
        public required DefaultHttpContext Http { get; init; }
        public required ResourceExecutingContext Context { get; init; }
        public ResourceExecutionDelegate Next { get; set; } = null!;
        public required CapturingResponseFeature Response { get; init; }
        public required FakeBodySizeFeature BodySize { get; init; }
        public required FakeMinRateFeature MinRate { get; init; }
        public bool NextCalled { get; set; }
    }

    private Harness CreateHarness(Guid uploadId, string? contentType = Multipart, long? contentLength = 100)
    {
        var response = new CapturingResponseFeature();
        var bodySize = new FakeBodySizeFeature();
        var minRate = new FakeMinRateFeature();
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(response);
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(bodySize);
        http.Features.Set<IHttpMinRequestBodyDataRateFeature>(minRate);
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = contentType;
        http.Request.ContentLength = contentLength;
        // Любое чтение тела до решения фильтра — провал теста.
        http.Request.Body = new UnreadableStream();

        var routeData = new RouteData();
        routeData.Values["uploadId"] = uploadId.ToString();
        var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
        var filters = new List<IFilterMetadata>();
        var context = new ResourceExecutingContext(actionContext, filters, new List<IValueProviderFactory>());

        var harness = new Harness
        {
            Http = http,
            Context = context,
            Response = response,
            BodySize = bodySize,
            MinRate = minRate
        };
        harness.Next = () =>
        {
            harness.NextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(actionContext, filters));
        };
        return harness;
    }

    private LegacyUploadAdmissionFilter CreateSut()
    {
        _budget = new LegacyUploadBudget(_options);
        var guard = new LegacyUploadQuotaGuard(_database.Context, _quota.Object, TimeProvider.System, _options);
        return new LegacyUploadAdmissionFilter(
            guard, _budget, _options, _metrics, NullLogger<LegacyUploadAdmissionFilter>.Instance);
    }

    private async Task<Guid> AddPlaceholderAsync(bool ready = false)
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            UploadedAt = ready ? DateTime.UtcNow : null,
            Etag = ready ? "etag" : null,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();
        return fileId;
    }

    private void SetupQuota(long? limit, long used = 0) =>
        _quota.Setup(x => x.GetSnapshotAsync(42, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(limit, used, 0));

    private static void AssertRejectedBeforeBody(Harness harness)
    {
        harness.NextCalled.Should().BeFalse();
        harness.Context.Result.Should().NotBeNull();
        harness.Http.Response.Headers.Connection.ToString().Should().Be("close");
    }

    [Fact]
    public async Task UnknownUploadId_IsRejectedWith404WithoutReadingBody()
    {
        var sut = CreateSut();
        var harness = CreateHarness(Guid.NewGuid(), contentLength: 50L * 1024 * 1024 * 1024);

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        AssertRejectedBeforeBody(harness);
        harness.Context.Result.Should().BeOfType<NotFoundObjectResult>();
        _metrics.SnapshotAndReset().Should().ContainKey("legacy_upload_rejected_total");
        _budget.BufferedBytes.Should().Be(0);
    }

    [Fact]
    public async Task NonMultipartBody_IsRejectedWith415()
    {
        var sut = CreateSut();
        var harness = CreateHarness(Guid.NewGuid(), contentType: "application/octet-stream");

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        AssertRejectedBeforeBody(harness);
        ((ObjectResult)harness.Context.Result!).StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
    }

    [Fact]
    public async Task AlreadyUploadedFile_IsRejectedWith400()
    {
        var fileId = await AddPlaceholderAsync(ready: true);
        var sut = CreateSut();
        var harness = CreateHarness(fileId);

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        AssertRejectedBeforeBody(harness);
        harness.Context.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ContentLengthAboveCap_IsRejectedWith413()
    {
        var fileId = await AddPlaceholderAsync();
        SetupQuota(limit: null);
        var sut = CreateSut();
        var harness = CreateHarness(fileId, contentLength: 1000 + 100 + 1);

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        AssertRejectedBeforeBody(harness);
        ((ObjectResult)harness.Context.Result!).StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        _budget.BufferedBytes.Should().Be(0);
    }

    [Fact]
    public async Task ContentLengthAboveQuotaRemainder_IsRejectedWith409AndQuotaPayload()
    {
        var fileId = await AddPlaceholderAsync();
        SetupQuota(limit: 500, used: 400);
        var sut = CreateSut();
        var harness = CreateHarness(fileId, contentLength: 100 + 200);

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        AssertRejectedBeforeBody(harness);
        harness.Context.Result.Should().BeOfType<ConflictObjectResult>();
        _metrics.SnapshotAndReset().Should().ContainKey("upload_quota_rejections_total");
    }

    [Fact]
    public async Task WhenBudgetIsBusy_IsRejectedWith503AndRetryAfter()
    {
        var fileId = await AddPlaceholderAsync();
        SetupQuota(limit: null);
        _options.MaxConcurrent = 1;
        var sut = CreateSut();
        using var occupied = await _budget.TryAcquireAsync(1, default);
        var harness = CreateHarness(fileId);

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        AssertRejectedBeforeBody(harness);
        ((StatusCodeResult)harness.Context.Result!).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        harness.Http.Response.Headers.RetryAfter.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AdmittedRequest_SetsPerRequestLimitsAndReleasesBudgetOnCompletion()
    {
        var fileId = await AddPlaceholderAsync();
        SetupQuota(limit: null);
        var sut = CreateSut();
        var harness = CreateHarness(fileId, contentLength: 800);

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        harness.NextCalled.Should().BeTrue();
        harness.Context.Result.Should().BeNull();
        harness.BodySize.MaxRequestBodySize.Should().Be(1000 + 100);
        harness.MinRate.MinDataRate.Should().NotBeNull();
        harness.MinRate.MinDataRate!.BytesPerSecond.Should().Be(_options.MinBytesPerSecond);
        harness.MinRate.MinDataRate.GracePeriod.Should().Be(_options.MinRateGrace);
        _budget.BufferedBytes.Should().Be(800);

        await harness.Response.FireOnCompletedAsync();

        _budget.BufferedBytes.Should().Be(0);
    }

    [Fact]
    public async Task AdmittedRequestOverHttp2_SkipsMinDataRateAndStillAdmits()
    {
        var fileId = await AddPlaceholderAsync();
        SetupQuota(limit: null);
        var sut = CreateSut();
        var harness = CreateHarness(fileId, contentLength: 800);
        harness.Http.Request.Protocol = "HTTP/2";
        harness.Http.Features.Set<IHttpMinRequestBodyDataRateFeature>(new Http2MinRateFeature());

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        harness.NextCalled.Should().BeTrue();
        harness.Context.Result.Should().BeNull();
        harness.BodySize.MaxRequestBodySize.Should().Be(1000 + 100);
        _budget.BufferedBytes.Should().Be(800);

        await harness.Response.FireOnCompletedAsync();

        _budget.BufferedBytes.Should().Be(0);
    }

    [Fact]
    public async Task ChunkedRequest_ReservesWholeCapInBudget()
    {
        var fileId = await AddPlaceholderAsync();
        SetupQuota(limit: null);
        var sut = CreateSut();
        var harness = CreateHarness(fileId, contentLength: null);

        await sut.OnResourceExecutionAsync(harness.Context, harness.Next);

        harness.NextCalled.Should().BeTrue();
        _budget.BufferedBytes.Should().Be(1000 + 100);
    }

    public void Dispose() => _database.Dispose();

    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Тело запроса прочитано до решения фильтра.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Тело запроса прочитано до решения фильтра.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingResponseFeature : HttpResponseFeature
    {
        private readonly List<Func<Task>> _callbacks = [];

        public override void OnCompleted(Func<object, Task> callback, object state) =>
            _callbacks.Add(() => callback(state));

        public async Task FireOnCompletedAsync()
        {
            foreach (var callback in _callbacks)
                await callback();
        }
    }

    private sealed class FakeBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class FakeMinRateFeature : IHttpMinRequestBodyDataRateFeature
    {
        public MinDataRate? MinDataRate { get; set; }
    }

    /// <summary>Как Kestrel на HTTP/2: per-request минимальная скорость не поддерживается.</summary>
    private sealed class Http2MinRateFeature : IHttpMinRequestBodyDataRateFeature
    {
        public MinDataRate? MinDataRate
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
