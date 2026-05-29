using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

namespace BarkCloud.Files.Tests.Services;

public class AlbumViewBuilderTests
{
    private readonly Mock<IAlbumStorage> _albums = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private AlbumViewBuilder CreateSut() => new(
        _albums.Object, _files.Object,
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty());

    [Fact]
    public async Task Build_Empty_ReturnsEmpty()
    {
        var result = await CreateSut().BuildAsync(new List<Album>(), default);

        result.Should().BeEmpty();
        _albums.Verify(s => s.GetItemCounts(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Build_NoCover_MapsCountWithoutCoverUrl()
    {
        var album = new Album { Id = Guid.NewGuid(), Name = "Trip" };
        _albums.Setup(s => s.GetItemCounts(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int> { [album.Id] = 7 });

        var result = await CreateSut().BuildAsync(new List<Album> { album }, default);

        var info = result.Should().ContainSingle().Subject;
        info.ItemsCount.Should().Be(7);
        info.CoverPreviewUrl.Should().BeEmpty();
        _files.Verify(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Build_CoverWithPreferredWidth_PicksThatPreview()
    {
        var coverId = Guid.NewGuid();
        var preferredPreviewId = Guid.NewGuid();
        var album = new Album { Id = Guid.NewGuid(), Name = "Trip", CoverFileId = coverId };
        _albums.Setup(s => s.GetItemCounts(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int> { [album.Id] = 3 });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>
            {
                [coverId] = new()
                {
                    new() { PreviewFileId = Guid.NewGuid(), TargetWidth = 128 },
                    new() { PreviewFileId = preferredPreviewId, TargetWidth = 512 },
                    new() { PreviewFileId = Guid.NewGuid(), TargetWidth = 1024 },
                }
            });

        var result = await CreateSut().BuildAsync(new List<Album> { album }, default);

        var info = result.Should().ContainSingle().Subject;
        info.ItemsCount.Should().Be(3);
        info.CoverPreviewUrl.Should().Contain(preferredPreviewId.ToString());
    }

    [Fact]
    public async Task Build_CoverWithoutPreferredWidth_FallsBackToLastPreview()
    {
        var coverId = Guid.NewGuid();
        var lastPreviewId = Guid.NewGuid();
        var album = new Album { Id = Guid.NewGuid(), Name = "Trip", CoverFileId = coverId };
        _albums.Setup(s => s.GetItemCounts(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>
            {
                [coverId] = new()
                {
                    new() { PreviewFileId = Guid.NewGuid(), TargetWidth = 128 },
                    new() { PreviewFileId = lastPreviewId, TargetWidth = 1024 },
                }
            });

        var result = await CreateSut().BuildAsync(new List<Album> { album }, default);

        var info = result.Should().ContainSingle().Subject;
        info.ItemsCount.Should().Be(0);
        info.CoverPreviewUrl.Should().Contain(lastPreviewId.ToString());
    }
}
