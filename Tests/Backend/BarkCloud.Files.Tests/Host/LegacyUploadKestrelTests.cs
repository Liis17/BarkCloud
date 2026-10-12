using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using BarkCloud.Files.Domain;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Host;

public class LegacyUploadKestrelTests : IAsyncLifetime
{
    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IStorageQuotaService> _quota = new(MockBehavior.Strict);
    private readonly Mock<IMediator> _mediator = new(MockBehavior.Strict);
    private readonly Mock<IMultipartUploadStore> _objects = new(MockBehavior.Strict);
    private readonly Mock<IUploadProcessingPublisher> _processing = new(MockBehavior.Strict);
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private long _bodyBytesRead;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            o.Limits.MinRequestBodyDataRate = null;
            o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1);
        });

        builder.Services.AddControllers()
            .PartManager.ApplicationParts.Add(new AssemblyPart(typeof(FilesController).Assembly));
        builder.Services.AddSingleton<IMediator>(_mediator.Object);
        builder.Services.AddSingleton(new MetricsCollector());
        builder.Services.AddSingleton<IStorageQuotaService>(_quota.Object);
        builder.Services.AddSingleton<IMultipartUploadStore>(_objects.Object);
        builder.Services.AddSingleton<IUploadProcessingPublisher>(_processing.Object);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_database.Context);
        builder.Services.AddScoped(_ => new UploadSessionCoordinator(
            _database.Context,
            _quota.Object,
            _objects.Object,
            _processing.Object,
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

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _database.Dispose();
    }

    [Theory]
    [InlineData(UploadFileType.CloudFile)]
    [InlineData(UploadFileType.UserAvatar)]
    public async Task UploadFile_Returns418WithoutReadingBodyOrCreatingReservation(UploadFileType type)
    {
        var fileId = Guid.NewGuid();
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = type,
            StorageProfileId = "universal-v1"
        });
        await _database.Context.SaveChangesAsync();

        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(new byte[1024 * 1024]);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "file", "legacy.bin");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/upload/{fileId}")
        {
            Content = content
        };
        request.Headers.ExpectContinue = true;

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be((HttpStatusCode)418);
        response.Version.Should().Be(HttpVersion.Version11);
        response.Headers.ConnectionClose.Should().BeTrue();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString()
            .Should().Be("Загрузка через этот адрес больше не поддерживается, используйте Upload 2.0 (CreateUploadSession).");
        body.RootElement.GetProperty("code").GetString().Should().Be("legacy_upload_removed");

        Interlocked.Read(ref _bodyBytesRead).Should().Be(0);
        _database.Context.UploadSessions.Should().BeEmpty();
        var file = _database.Context.UploadedFiles.Single();
        file.UploadedAt.Should().BeNull();
        file.Etag.Should().BeNull();
        _quota.VerifyNoOtherCalls();
        _mediator.VerifyNoOtherCalls();
        _objects.VerifyNoOtherCalls();
        _processing.VerifyNoOtherCalls();
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

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
