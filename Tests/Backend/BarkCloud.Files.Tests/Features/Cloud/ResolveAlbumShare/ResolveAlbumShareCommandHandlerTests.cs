using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ResolveAlbumShare;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;
using AlbumEntity = BarkCloud.Files.Domain.Album;

namespace BarkCloud.Files.Tests.Features.Cloud.ResolveAlbumShare;

public class ResolveAlbumShareCommandHandlerTests
{
    [Fact]
    public async Task Handle_VideoItemIncludesQualityLinks()
    {
        const long ownerId = 42;
        var albumId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var originalTempId = Guid.NewGuid();
        var variantTempId = Guid.NewGuid();
        var shares = new Mock<IAlbumShareStorage>();
        var albums = new Mock<IAlbumStorage>();
        var hierarchy = new Mock<ICloudHierarchyStorage>();
        var files = new Mock<IUploadedFilesStorage>();
        var tempFiles = new Mock<ITempFilesStorage>();
        var addedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
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
            .ReturnsAsync(new AlbumShareLink { Id = Guid.NewGuid(), OwnerId = ownerId, AlbumId = albumId, Token = "token" });
        albums.Setup(x => x.GetAlbum(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlbumEntity { Id = albumId, OwnerId = ownerId, Name = "Videos" });
        albums.Setup(x => x.ListItemsPage(albumId, null, null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlbumItem> { new() { AlbumId = albumId, OwnerId = ownerId, FileId = fileId, AddedAt = addedAt } });
        files.Setup(x => x.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync([video]);
        files.Setup(x => x.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        files.Setup(x => x.GetVideoVariantsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<VideoVariantDownloadInfo>>
            {
                [fileId] = [new(variantId, 360, 640, 360, 300)]
            });
        hierarchy.Setup(x => x.GetEffectivelyTrashedFileIds(ownerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid>());
        tempFiles.Setup(x => x.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFile>
            {
                new() { Id = originalTempId, OriginalFileId = fileId },
                new() { Id = variantTempId, OriginalFileId = variantId }
            });

        var handler = new ResolveAlbumShareCommandHandler(
            shares.Object,
            albums.Object,
            files.Object,
            hierarchy.Object,
            tempFiles.Object,
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty(),
            NullLogger<ResolveAlbumShareCommandHandler>.Instance);

        var response = await handler.Handle(new ResolveAlbumShareCommand { Token = "token" }, default);

        response.Found.Should().BeTrue();
        response.Items.Should().ContainSingle();
        response.Items[0].VideoQualities.Should().HaveCount(2);
        response.Items[0].VideoQualities[0].IsOriginal.Should().BeTrue();
        response.Items[0].VideoQualities[1].IsOriginal.Should().BeFalse();
        response.Items[0].VideoQualities[1].Url.Should().Contain(variantTempId.ToString());
    }
}
