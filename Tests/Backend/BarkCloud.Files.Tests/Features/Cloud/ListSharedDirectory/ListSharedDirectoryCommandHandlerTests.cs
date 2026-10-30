using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListSharedDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;

using Microsoft.AspNetCore.Http;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListSharedDirectory;

public class ListSharedDirectoryCommandHandlerTests
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
        var grants = new Mock<IDirectoryGrantStorage>();
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

        grants.Setup(x => x.ListByRecipient(0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DirectoryGrant> { new() { OwnerId = ownerId, DirectoryId = directoryId } });
        hierarchy.Setup(x => x.GetSubtree(ownerId, directoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CloudDirectory> { directory });
        hierarchy.Setup(x => x.GetDirectoryAsNoTracking(directoryId, It.IsAny<CancellationToken>())).ReturnsAsync(directory);
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

        var access = new FolderGrantAccessService(grants.Object, hierarchy.Object);
        var userContext = new UserContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var handler = new ListSharedDirectoryCommandHandler(
            access,
            hierarchy.Object,
            files.Object,
            tempFiles.Object,
            userContext,
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        var response = await handler.Handle(new ListSharedDirectoryCommand { DirectoryId = directoryId }, default);

        response.Found.Should().BeTrue();
        response.Files.Should().ContainSingle();
        response.Files[0].VideoQualities.Should().HaveCount(2);
        response.Files[0].VideoQualities[0].IsOriginal.Should().BeTrue();
        response.Files[0].VideoQualities[1].IsOriginal.Should().BeFalse();
        response.Files[0].VideoQualities[1].Url.Should().Contain(variantTempId.ToString());
    }
}
