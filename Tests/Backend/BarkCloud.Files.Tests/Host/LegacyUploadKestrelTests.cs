using System.Net;
using System.Net.Http.Headers;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.UploadFile;
using BarkCloud.Files.Host;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Metrics;

using MediatR;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Host;

/// <summary>
/// Проверка на настоящем Kestrel с реальным <see cref="FilesController"/>: фильтр допуска срабатывает
/// до чтения тела, а лимит размера на запрос действительно обрывает слишком большое тело.
/// </summary>
public class LegacyUploadKestrelTests : IAsyncLifetime
{
    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IStorageQuotaService> _quota = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly LegacyUploadOptions _options = new()
    {
        MaxFileBytes = 4096,
        MultipartOverheadBytes = 1024
    };
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private HttpClient _http2Client = null!;
    private long _bodyBytesRead;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Как в проде: отдельные порты HTTP/1 (веб) и HTTP/2 (gRPC); MVC-контроллеры отдаются на обоих.
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            o.Limits.MinRequestBodyDataRate = null;
            o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1);
            o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2);
        });

        _quota.Setup(x => x.GetSnapshotAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));

        builder.Services.AddControllers()
            .PartManager.ApplicationParts.Add(new AssemblyPart(typeof(FilesController).Assembly));
        builder.Services.AddSingleton(_mediator.Object);
        builder.Services.AddSingleton(new MetricsCollector());
        builder.Services.AddSingleton(_options);
        builder.Services.AddSingleton<LegacyUploadBudget>();
        builder.Services.AddSingleton(_quota.Object);
        builder.Services.AddSingleton(TimeProvider.System);
        // Один общий контекст на всё тестовое приложение (SQLite in-memory + один запрос за раз).
        builder.Services.AddSingleton(_database.Context);
        builder.Services.AddScoped<LegacyUploadQuotaGuard>();
        builder.Services.AddScoped<LegacyUploadAdmissionFilter>();
        builder.Services.AddScoped(_ => new UploadSessionCoordinator(
            _database.Context,
            _quota.Object,
            new Mock<IMultipartUploadStore>().Object,
            new Mock<IUploadProcessingPublisher>().Object,
            new S3BucketRegistry(TestConfiguration.With(
                ("StorageProfiles:universal:ProfileId", "universal-v1"),
                ("StorageProfiles:universal:Role", "universal"),
                ("StorageProfiles:universal:Version", "1"),
                ("StorageProfiles:universal:ServiceUrl", "http://localhost:9000"),
                ("StorageProfiles:universal:AccessKey", "test"),
                ("StorageProfiles:universal:SecretKey", "test-secret"),
                ("StorageProfiles:universal:BucketName", "files"),
                ("StorageProfiles:universal:IsActive", "true"))),
            TimeProvider.System,
            NullLogger<UploadSessionCoordinator>.Instance));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            var counting = new CountingStream(context.Request.Body);
            context.Request.Body = counting;
            try
            {
                await next();
            }
            finally
            {
                Interlocked.Add(ref _bodyBytesRead, counting.BytesRead);
            }
        });
        _app.MapControllers();
        await _app.StartAsync();

        // Адреса идут в порядке Listen: HTTP/1, затем HTTP/2 (h2c с предварительным знанием).
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.ToList();
        _client = new HttpClient { BaseAddress = new Uri(addresses[0]), Timeout = TimeSpan.FromSeconds(30) };
        _http2Client = new HttpClient
        {
            BaseAddress = new Uri(addresses[1]),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _http2Client.Dispose();
        await _app.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> AddPlaceholderAsync()
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();
        return fileId;
    }

    private static MultipartFormDataContent Multipart(Stream body)
    {
        var part = new StreamContent(body);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { part, "file", "big.bin" } };
    }

    [Fact]
    public async Task UnknownUploadId_Returns404AndNoBodyBytesAreRead()
    {
        using var content = Multipart(new ZeroStream(20L * 1024 * 1024));

        using var response = await _client.PostAsync($"/upload/{Guid.NewGuid()}", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Interlocked.Read(ref _bodyBytesRead).Should().Be(0);
    }

    [Fact]
    public async Task ChunkedBodyAboveCap_IsCutByKestrelBeforeProcessing()
    {
        var fileId = await AddPlaceholderAsync();
        // Неизвестная длина (chunked): отказ по Content-Length невозможен, режет только лимит Kestrel.
        _options.MaxFileBytes = 200 * 1024;
        using var content = Multipart(new ZeroStream(5L * 1024 * 1024, seekable: false));

        // Kestrel обрывает соединение посреди тела: клиент видит ответ-отказ либо сброс сокета.
        try
        {
            using var response = await _client.PostAsync($"/upload/{fileId}", content);
            response.IsSuccessStatusCode.Should().BeFalse();
        }
        catch (HttpRequestException)
        {
        }

        Interlocked.Read(ref _bodyBytesRead).Should().BeLessThanOrEqualTo(
            _options.MaxFileBytes + _options.MultipartOverheadBytes);
        _mediator.Verify(
            x => x.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _app.Services.GetRequiredService<LegacyUploadBudget>().BufferedBytes.Should().Be(0);
    }

    [Fact]
    public async Task KnownLengthAboveCap_Returns413WithoutReadingBody()
    {
        var fileId = await AddPlaceholderAsync();
        using var content = Multipart(new ZeroStream(5L * 1024 * 1024));

        using var response = await _client.PostAsync($"/upload/{fileId}", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        Interlocked.Read(ref _bodyBytesRead).Should().Be(0);
    }

    [Fact]
    public async Task ValidUpload_ReachesHandlerAndReleasesBudget()
    {
        var fileId = await AddPlaceholderAsync();
        SetupSuccessfulUpload(fileId);
        using var content = Multipart(new ZeroStream(2048));

        using var response = await _client.PostAsync($"/upload/{fileId}", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(fileId.ToString());
        _app.Services.GetRequiredService<LegacyUploadBudget>().BufferedBytes.Should().Be(0);
    }

    [Fact]
    public async Task ValidUpload_OverHttp2_ReachesHandlerAndReleasesBudget()
    {
        var fileId = await AddPlaceholderAsync();
        SetupSuccessfulUpload(fileId);
        using var content = Multipart(new ZeroStream(2048));

        using var response = await _http2Client.PostAsync($"/upload/{fileId}", content);

        response.Version.Should().Be(HttpVersion.Version20);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(fileId.ToString());
        _app.Services.GetRequiredService<LegacyUploadBudget>().BufferedBytes.Should().Be(0);
    }

    private void SetupSuccessfulUpload(Guid fileId)
    {
        _mediator.Setup(x => x.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async (UploadFileCommand command, CancellationToken _) =>
            {
                var file = _database.Context.UploadedFiles.Single(x => x.Id == fileId);
                file.Etag = "etag";
                file.Size = command.FileSize;
                await _database.Context.SaveChangesAsync();
                var guard = new LegacyUploadQuotaGuard(
                    _database.Context, _quota.Object, TimeProvider.System, _options);
                await guard.MarkProcessingCompletedAsync(command.QuotaReservationId!.Value, CancellationToken.None);
                return fileId.ToString();
            });
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ZeroStream(long length, bool seekable = true) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => _position = seekable ? value : throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, take);
            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!seekable)
                throw new NotSupportedException();
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset
            };
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
