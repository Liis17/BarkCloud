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
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Etag = "e", Filename = "doc.pdf" });
        _files.Setup(s => s.IsPreviewFile(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var act = () => CreateSut().Handle(new DownloadFileCommand { FileId = id }, default);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Handle_UserAvatar_DownloadsFromS3()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.UserAvatar, Etag = "e", Filename = "a.png" });
        _files.Setup(s => s.IsPreviewFile(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _s3.Setup(s => s.DownloadAsync("test-bucket", id.ToString())).ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        var result = await CreateSut().Handle(new DownloadFileCommand { FileId = id }, default);

        result.FileName.Should().Be($"{id}.png");
        result.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task Handle_PreviewCloudFile_DownloadsFromS3()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Etag = "e", Filename = "p.jpg" });
        _files.Setup(s => s.IsPreviewFile(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _s3.Setup(s => s.DownloadAsync("test-bucket", id.ToString())).ReturnsAsync(new MemoryStream());

        var result = await CreateSut().Handle(new DownloadFileCommand { FileId = id }, default);

        result.FileName.Should().Be($"{id}.jpg");
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
            .ReturnsAsync(new UploadFileEntity { Id = originalId, Type = UploadFileType.CloudFile, Etag = "e", Filename = "file.txt" });
        _s3.Setup(s => s.DownloadAsync("test-bucket", originalId.ToString())).ReturnsAsync(new MemoryStream());

        var result = await CreateSut().Handle(new DownloadFileCommand { FileId = tempId }, default);

        result.FileName.Should().Be($"{originalId}.txt");
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
