using BarkCloud.Files.Domain;
using BarkCloud.Files.Host;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.TestKit;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using Microsoft.Extensions.Logging.Abstractions;

using DomainUploadFileType = BarkCloud.Files.Domain.UploadFileType;
using ProtoUploadFileType = BarkCloud.Proto.Files.UploadFileType;

namespace BarkCloud.Files.Tests.Host;

public sealed class FilesApiServiceUploadSessionTests : IDisposable
{
    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IStorageLimitProvider> _limits = new();
    private readonly Mock<IMultipartUploadStore> _objects = new();
    private readonly S3BucketRegistry _profiles;

    public FilesApiServiceUploadSessionTests()
    {
        _profiles = new S3BucketRegistry(TestConfiguration.With(
            ("StorageProfiles:universal:ProfileId", "universal-v1"),
            ("StorageProfiles:universal:Role", "universal"),
            ("StorageProfiles:universal:Version", "1"),
            ("StorageProfiles:universal:ServiceUrl", "http://localhost:9000"),
            ("StorageProfiles:universal:AccessKey", "test"),
            ("StorageProfiles:universal:SecretKey", "test-secret"),
            ("StorageProfiles:universal:BucketName", "files"),
            ("StorageProfiles:universal:IsActive", "true"),
            ("StorageProfiles:avatars:ProfileId", "avatars-v1"),
            ("StorageProfiles:avatars:Role", "avatars"),
            ("StorageProfiles:avatars:Version", "1"),
            ("StorageProfiles:avatars:ServiceUrl", "http://localhost:9000"),
            ("StorageProfiles:avatars:AccessKey", "test"),
            ("StorageProfiles:avatars:SecretKey", "test-secret"),
            ("StorageProfiles:avatars:BucketName", "avatars"),
            ("StorageProfiles:avatars:IsActive", "true")));
        _limits.Setup(x => x.GetLimitBytesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);
        _objects.Setup(x => x.InitiateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("multipart");
    }

    [Fact]
    public async Task CreateUploadSession_AbsentFileTypeDefaultsToCloudFile()
    {
        var request = Request();
        request.HasFileType.Should().BeFalse();

        var response = await CreateSut().CreateUploadSession(request, new TestServerCallContext());

        var session = _database.Context.UploadSessions.Single(x => x.Id == Guid.Parse(response.SessionId));
        var file = _database.Context.UploadedFiles.Single(x => x.Id == Guid.Parse(response.FileId));
        session.FileType.Should().Be(DomainUploadFileType.CloudFile);
        file.Type.Should().Be(DomainUploadFileType.CloudFile);
        session.StorageProfileId.Should().Be("universal-v1");
    }

    [Theory]
    [InlineData(ProtoUploadFileType.CloudFile, DomainUploadFileType.CloudFile, "file.bin", "application/octet-stream", "universal-v1")]
    [InlineData(ProtoUploadFileType.UserAvatar, DomainUploadFileType.UserAvatar, "avatar.png", "image/png", "avatars-v1")]
    public async Task CreateUploadSession_ExplicitSupportedTypeIsMapped(
        ProtoUploadFileType protoType,
        DomainUploadFileType expectedType,
        string fileName,
        string contentType,
        string expectedProfileId)
    {
        var request = Request(fileName, contentType);
        request.FileType = protoType;

        var response = await CreateSut().CreateUploadSession(request, new TestServerCallContext());

        request.HasFileType.Should().BeTrue();
        var session = _database.Context.UploadSessions.Single(x => x.Id == Guid.Parse(response.SessionId));
        var file = _database.Context.UploadedFiles.Single(x => x.Id == Guid.Parse(response.FileId));
        session.FileType.Should().Be(expectedType);
        file.Type.Should().Be(expectedType);
        session.StorageProfileId.Should().Be(expectedProfileId);
        _objects.Verify(x => x.InitiateAsync(
            expectedProfileId, response.FileId, contentType, fileName, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ProtoUploadFileType.Unknown)]
    [InlineData((ProtoUploadFileType)999)]
    public async Task CreateUploadSession_ExplicitUnknownOrUnsupportedTypeIsRejected(ProtoUploadFileType fileType)
    {
        var request = Request();
        request.FileType = fileType;

        var act = () => CreateSut().CreateUploadSession(request, new TestServerCallContext());

        await act.Should().ThrowAsync<InvalidUploadSessionRequestException>();
        _limits.Verify(x => x.GetLimitBytesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _objects.Verify(x => x.InitiateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _database.Context.UploadSessions.Should().BeEmpty();
    }

    private FilesApiService CreateSut()
    {
        var coordinator = new UploadSessionCoordinator(
            _database.Context,
            new StorageQuotaService(_database.Context, _limits.Object),
            _objects.Object,
            Mock.Of<IUploadProcessingPublisher>(),
            _profiles,
            TimeProvider.System,
            NullLogger<UploadSessionCoordinator>.Instance);
        return new FilesApiService(
            Mock.Of<IMediator>(),
            new MetricsCollector(),
            coordinator,
            new RequestContext(),
            UserContextFactory.Create(42));
    }

    private static CreateUploadSessionRequest Request(string fileName = "file.bin", string contentType = "application/octet-stream") => new()
    {
        IdempotencyKey = "api-upload",
        FileName = fileName,
        FileSize = 42,
        ContentType = contentType,
        Sha256 = new string('a', 64)
    };

    public void Dispose()
    {
        _profiles.Dispose();
        _database.Dispose();
    }
}
