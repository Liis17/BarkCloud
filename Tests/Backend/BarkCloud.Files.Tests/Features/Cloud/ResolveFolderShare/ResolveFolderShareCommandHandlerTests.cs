using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ResolveFolderShare;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ResolveFolderShare;

public class ResolveFolderShareCommandHandlerTests
{
    [Fact]
    public async Task Handle_VideoEntryIncludesQualityLinks()
    {
        const long ownerId = 42;
        var directoryId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var originalTempId = Guid.NewGuid();
        var variantTempId = Guid.NewGuid();
        var shares = new Mock<IFolderShareStorage>();
        var hierarchy = new Mock<ICloudHierarchyStorage>();
        var files = new Mock<IUploadedFilesStorage>();
        var tempFiles = new Mock<ITempFilesStorage>();
        var directory = new CloudDirectory { Id = directoryId, OwnerId = ownerId, Name = "Shared" };
        var video = new UploadFileEntity
        {
            Id = fileId,
            Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Video,
            ImageWidth = 1920,
            ImageHeight = 1080,
            Size = 1000,
            Etag = "etag",
            UploadedAt = DateTime.UtcNow
        };
        shares.Setup(x => x.GetByToken("token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FolderShareLink { Id = Guid.NewGuid(), OwnerId = ownerId, DirectoryId = directoryId, Token = "token" });
        hierarchy.Setup(x => x.GetDirectoryAsNoTracking(directoryId, It.IsAny<CancellationToken>())).ReturnsAsync(directory);
        hierarchy.Setup(x => x.GetSubtree(ownerId, directoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudDirectory> { directory });
        hierarchy.Setup(x => x.ListSubdirectories(ownerId, directoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudDirectory>());
        hierarchy.Setup(x => x.ListFilesInDirectory(ownerId, directoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>
            {
                new() { Id = Guid.NewGuid(), OwnerId = ownerId, DirectoryId = directoryId, FileId = fileId, Name = "video.mp4" }
            });
        files.Setup(x => x.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync([video]);
        files.Setup(x => x.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        files.Setup(x => x.GetVideoVariantsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<VideoVariantDownloadInfo>>
            {
                [fileId] = [new(variantId, 360, 640, 360, 300)]
            });
        tempFiles.Setup(x => x.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFile>
            {
                new() { Id = originalTempId, OriginalFileId = fileId },
                new() { Id = variantTempId, OriginalFileId = variantId }
            });

        var handler = new ResolveFolderShareCommandHandler(
            shares.Object,
            hierarchy.Object,
            files.Object,
            tempFiles.Object,
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty(),
            NullLogger<ResolveFolderShareCommandHandler>.Instance);

        var response = await handler.Handle(new ResolveFolderShareCommand { Token = "token" }, default);

        response.Files.Should().ContainSingle();
        response.Files[0].VideoQualities.Should().HaveCount(2);
        response.Files[0].VideoQualities[0].IsOriginal.Should().BeTrue();
        response.Files[0].VideoQualities[1].IsOriginal.Should().BeFalse();
        response.Files[0].VideoQualities[1].Url.Should().Contain(variantTempId.ToString());
    }

    [Fact]
    public async Task Handle_FileIsNotReady_HidesItAndDoesNotCreateDownloadToken()
    {
        const long ownerId = 42;
        var directoryId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var shares = new Mock<IFolderShareStorage>();
        var hierarchy = new Mock<ICloudHierarchyStorage>();
        var files = new Mock<IUploadedFilesStorage>();
        var tempFiles = new Mock<ITempFilesStorage>();
        var directory = new CloudDirectory { Id = directoryId, OwnerId = ownerId, Name = "Shared" };

        shares.Setup(x => x.GetByToken("token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FolderShareLink { Id = Guid.NewGuid(), OwnerId = ownerId, DirectoryId = directoryId, Token = "token" });
        hierarchy.Setup(x => x.GetDirectoryAsNoTracking(directoryId, It.IsAny<CancellationToken>())).ReturnsAsync(directory);
        hierarchy.Setup(x => x.GetSubtree(ownerId, directoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudDirectory> { directory });
        hierarchy.Setup(x => x.ListSubdirectories(ownerId, directoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudDirectory>());
        hierarchy.Setup(x => x.ListFilesInDirectory(ownerId, directoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudFileEntry>
            {
                new() { Id = Guid.NewGuid(), OwnerId = ownerId, DirectoryId = directoryId, FileId = fileId, Name = "processing.mov" }
            });
        files.Setup(x => x.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>());
        files.Setup(x => x.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        tempFiles.Setup(x => x.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFile>());

        var handler = new ResolveFolderShareCommandHandler(
            shares.Object,
            hierarchy.Object,
            files.Object,
            tempFiles.Object,
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty(),
            NullLogger<ResolveFolderShareCommandHandler>.Instance);

        var response = await handler.Handle(new ResolveFolderShareCommand { Token = "token" }, default);

        response.Found.Should().BeTrue();
        response.Files.Should().BeEmpty();
        tempFiles.Verify(
            x => x.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
