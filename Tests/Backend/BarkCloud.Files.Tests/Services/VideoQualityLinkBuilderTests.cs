using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;

using Xunit;

using VideoQualityInfo = BarkCloud.Proto.Files.VideoQualityInfo;

namespace BarkCloud.Files.Tests.Services;

public sealed class VideoQualityLinkBuilderTests
{
    [Fact]
    public async Task CreateAsync_BatchesOriginalAndVariantTempsWithoutReturningVariantIds()
    {
        var originalId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var originalTempId = Guid.NewGuid();
        var variantTempId = Guid.NewGuid();
        var original = new UploadFile
        {
            Id = originalId,
            Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Video,
            ImageWidth = 1920,
            ImageHeight = 1080,
            Size = 1000
        };
        var files = new Mock<IUploadedFilesStorage>();
        files.Setup(storage => storage.GetVideoVariantsForFiles(
            It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { originalId })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<VideoVariantDownloadInfo>>
            {
                [originalId] = [new(variantId, 360, 640, 360, 300)]
            });
        var tempFiles = new Mock<ITempFilesStorage>();
        tempFiles.Setup(storage => storage.CreateTempFilesBatchAsync(
                It.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(new[] { originalId, variantId })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFile>
            {
                new() { Id = originalTempId, OriginalFileId = originalId },
                new() { Id = variantTempId, OriginalFileId = variantId }
            });

        var links = await VideoQualityLinkBuilder.CreateAsync(
            files.Object, tempFiles.Object, [original], "https://files.example/web", CancellationToken.None);

        var originalLinks = links[originalId];
        originalLinks.OriginalUrl.Should().Be($"https://files.example/web/download/{originalTempId}");
        originalLinks.VideoQualities.Should().HaveCount(2);
        originalLinks.VideoQualities[0].Should().BeEquivalentTo(new VideoQualityInfo
        {
            Height = 1080,
            Width = 1920,
            FileSize = 1000,
            IsOriginal = true,
            Url = originalLinks.OriginalUrl
        });
        originalLinks.VideoQualities[1].Should().BeEquivalentTo(new VideoQualityInfo
        {
            Height = 360,
            Width = 640,
            FileSize = 300,
            IsOriginal = false,
            Url = $"https://files.example/web/download/{variantTempId}"
        });
        tempFiles.Verify(storage => storage.CreateTempFilesBatchAsync(
            It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
