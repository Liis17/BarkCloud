using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Features.DownloadFile;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.DownloadFile;

public class DownloadFileCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ITempFilesStorage> _temp = new();
    private readonly Mock<S3BucketRegistry> _bucketRegistry;
    private readonly Mock<S3Uploader> _s3;

    public DownloadFileCommandHandlerTests()
    {
        _bucketRegistry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
        _bucketRegistry.Setup(r => r.GetBucketName(It.IsAny<UploadFileType>())).Returns("test-bucket");

        _s3 = new Mock<S3Uploader>(_bucketRegistry.Object) { CallBase = false };
    }

    private DownloadFileCommandHandler CreateSut() => new(
        _files.Object, _s3.Object, _bucketRegistry.Object, _temp.Object,
        NullLogger<DownloadFileCommandHandler>.Instance);

    [Fact]
    public async Task Handle_TempVideoAfterRelocation_ReadsRangeWithOriginalIdThroughInactiveProfile()
    {
        var configuration = TestConfiguration.With(
            ("StorageProfiles:videos-v1:Role", "videos"), ("StorageProfiles:videos-v1:Version", "1"),
            ("StorageProfiles:videos-v1:ServiceUrl", "https://destination.example"),
            ("StorageProfiles:videos-v1:BucketName", "cloud-video"),
            ("StorageProfiles:videos-v1:AccessKey", "target-key"), ("StorageProfiles:videos-v1:SecretKey", "target-secret"),
            ("StorageProfiles:videos-v2:Role", "videos"), ("StorageProfiles:videos-v2:Version", "2"),
            ("StorageProfiles:videos-v2:ServiceUrl", "https://destination.example"),
            ("StorageProfiles:videos-v2:BucketName", "cloud-video"),
            ("StorageProfiles:videos-v2:AccessKey", "target-key"), ("StorageProfiles:videos-v2:SecretKey", "target-secret"),
            ("StorageProfiles:videos-v2:IsActive", "true"));
        var profiles = new Mock<S3BucketRegistry>(configuration) { CallBase = true };
        using var registry = profiles.Object;
        var client = new Mock<IAmazonS3>(MockBehavior.Strict);
        profiles.Setup(x => x.GetClientForProfile("videos-v1")).Returns(client.Object);
        var tempId = Guid.NewGuid(); var originalId = Guid.NewGuid();
        _files.Setup(x => x.GetFile(tempId)).ReturnsAsync((UploadFileEntity?)null);
        _temp.Setup(x => x.GetTempFile(tempId)).ReturnsAsync(new TempFile { Id = tempId, OriginalFileId = originalId });
        _files.Setup(x => x.GetFile(originalId)).ReturnsAsync(new UploadFileEntity
        {
            Id = originalId, StorageProfileId = "videos-v1", Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Video, Filename = "video.mp4", Size = 2,
            Etag = "old-source-etag", UploadedAt = DateTime.UtcNow
        });
        client.Setup(x => x.GetObjectAsync(It.Is<GetObjectRequest>(r =>
            r.BucketName == "cloud-video" && r.Key == originalId.ToString()
            && r.ByteRange.Start == 0 && r.ByteRange.End == 1 && string.IsNullOrEmpty(r.EtagToMatch)),
            It.IsAny<CancellationToken>())).ReturnsAsync(new GetObjectResponse
            { ResponseStream = new MemoryStream([1, 2]), ContentLength = 2, ETag = "new-destination-etag" });
        var handler = new DownloadFileCommandHandler(_files.Object, new S3Uploader(registry), registry, _temp.Object,
            NullLogger<DownloadFileCommandHandler>.Instance);

        var result = await handler.Handle(new DownloadFileCommand { FileId = tempId, RangeStart = 0 }, default);

        await using var stream = result.FileStream;
        using var contents = new MemoryStream();
        await stream.CopyToAsync(contents);
        contents.ToArray().Should().Equal(1, 2);
        result.IsPartial.Should().BeTrue(); result.ContentLength.Should().Be(2);
        result.ContentType.Should().Be("video/mp4");
        registry.GetProfile("videos-v1").IsActive.Should().BeFalse();
        registry.GetProfile("videos-v1").ServiceUrl.Should().Be("https://destination.example");
        profiles.Verify(x => x.GetClientForProfile("videos-v1"), Times.Once);
        profiles.Verify(x => x.GetClientForProfile("videos-v2"), Times.Never);
    }

    [Fact]
    public async Task Handle_FileNotFoundAndNoTemp_Throws()
    {
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);
        _temp.Setup(s => s.GetTempFile(It.IsAny<Guid>())).ReturnsAsync((TempFile?)null);

        var act = () => CreateSut().Handle(new DownloadFileCommand { FileId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Handle_CloudFileTypeAndNotPreview_Throws()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Etag = "e", UploadedAt = DateTime.UtcNow, Filename = "doc.pdf" });
        _files.Setup(s => s.IsPreviewFile(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var act = () => CreateSut().Handle(new DownloadFileCommand { FileId = id }, default);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Handle_UserAvatar_DownloadsFromS3()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.UserAvatar, Etag = "e", UploadedAt = DateTime.UtcNow, Filename = "a.png" });
        _files.Setup(s => s.IsPreviewFile(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _s3.Setup(s => s.DownloadAsync("test-bucket", id.ToString())).ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        var result = await CreateSut().Handle(new DownloadFileCommand { FileId = id }, default);

        result.FileName.Should().Be("a.png");
        result.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task Handle_PreviewCloudFile_DownloadsFromS3()
    {
        var id = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Etag = "e", UploadedAt = DateTime.UtcNow, Filename = "p.jpg" });
        _files.Setup(s => s.IsPreviewFile(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _files.Setup(s => s.GetOriginalByPreviewFileId(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadFileEntity
            {
                Id = originalId,
                Type = UploadFileType.CloudFile,
                Etag = "original-etag",
                UploadedAt = DateTime.UtcNow
            });
        _s3.Setup(s => s.DownloadAsync("test-bucket", id.ToString())).ReturnsAsync(new MemoryStream());

        var result = await CreateSut().Handle(new DownloadFileCommand { FileId = id }, default);

        result.FileName.Should().Be("p.jpg");
    }

    [Fact]
    public async Task Handle_PreviewOfPendingOriginal_ThrowsFileNotUploaded()
    {
        var previewId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(previewId))
            .ReturnsAsync(new UploadFileEntity
            {
                Id = previewId,
                Type = UploadFileType.CloudFile,
                Etag = "preview-etag",
                UploadedAt = DateTime.UtcNow,
                Filename = "p.jpg"
            });
        _files.Setup(s => s.IsPreviewFile(previewId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _files.Setup(s => s.GetOriginalByPreviewFileId(previewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadFileEntity { Id = originalId, Type = UploadFileType.CloudFile });

        var act = () => CreateSut().Handle(new DownloadFileCommand { FileId = previewId }, default);

        await act.Should().ThrowAsync<FileNotUploadedException>();
        _s3.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_TempLink_ResolvesToOriginalAndDownloads()
    {
        var tempId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(tempId)).ReturnsAsync((UploadFileEntity?)null);
        _temp.Setup(s => s.GetTempFile(tempId))
            .ReturnsAsync(new TempFile { Id = tempId, OriginalFileId = originalId });
        _files.Setup(s => s.GetFile(originalId))
            .ReturnsAsync(new UploadFileEntity { Id = originalId, Type = UploadFileType.CloudFile, Etag = "e", UploadedAt = DateTime.UtcNow, Filename = "Мой файл.txt" });
        _s3.Setup(s => s.DownloadAsync("test-bucket", originalId.ToString())).ReturnsAsync(new MemoryStream());

        var result = await CreateSut().Handle(new DownloadFileCommand { FileId = tempId }, default);

        result.FileName.Should().Be("Мой файл.txt");
    }

    [Fact]
    public async Task Handle_FileWithoutEtag_ThrowsFileNotUploaded()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.UserAvatar, Etag = null, Filename = "a.png" });
        _files.Setup(s => s.IsPreviewFile(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var act = () => CreateSut().Handle(new DownloadFileCommand { FileId = id }, default);

        await act.Should().ThrowAsync<FileNotUploadedException>();
    }
}
