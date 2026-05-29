using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Album.ListAlbumItems;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainAlbum = BarkCloud.Files.Domain.Album;
using DomainAlbumItem = BarkCloud.Files.Domain.AlbumItem;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Album.ListAlbumItems;

public class ListAlbumItemsCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IAlbumStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();

    private ListAlbumItemsCommandHandler CreateSut() => new(
        _storage.Object, _files.Object, _hierarchy.Object,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
        NullLogger<ListAlbumItemsCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NotOwner_Throws()
    {
        var albumId = Guid.NewGuid();
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainAlbum { Id = albumId, OwnerId = 999 });

        var act = () => CreateSut().Handle(new ListAlbumItemsCommand { AlbumId = albumId, Limit = 50 }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_SkipsOrphanedAndTrashed_KeepsLiveFiles()
    {
        var albumId = Guid.NewGuid();
        var live = Guid.NewGuid();
        var trashed = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainAlbum { Id = albumId, OwnerId = OwnerId });
        _storage.Setup(s => s.ListItemsPage(albumId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainAlbumItem>
            {
                new() { AlbumId = albumId, FileId = live, AddedAt = DateTime.UtcNow },
                new() { AlbumId = albumId, FileId = trashed, AddedAt = DateTime.UtcNow },
                new() { AlbumId = albumId, FileId = orphan, AddedAt = DateTime.UtcNow },
            });
        // orphan отсутствует в files
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            new() { Id = live, MediaKind = MediaKind.Photo },
            new() { Id = trashed, MediaKind = MediaKind.Photo },
        });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetEffectivelyTrashedFileIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { trashed });

        var response = await CreateSut().Handle(new ListAlbumItemsCommand { AlbumId = albumId, Limit = 50 }, default);

        response.Items.Should().ContainSingle().Which.File.Id.Should().Be(live.ToString());
    }

    [Fact]
    public async Task Handle_KindFilter_KeepsOnlyMatchingKind()
    {
        var albumId = Guid.NewGuid();
        var photo = Guid.NewGuid();
        var video = Guid.NewGuid();
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainAlbum { Id = albumId, OwnerId = OwnerId });
        _storage.Setup(s => s.ListItemsPage(albumId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainAlbumItem>
            {
                new() { AlbumId = albumId, FileId = photo, AddedAt = DateTime.UtcNow },
                new() { AlbumId = albumId, FileId = video, AddedAt = DateTime.UtcNow },
            });
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            new() { Id = photo, MediaKind = MediaKind.Photo },
            new() { Id = video, MediaKind = MediaKind.Video },
        });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _hierarchy.Setup(s => s.GetEffectivelyTrashedFileIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid>());

        var response = await CreateSut().Handle(
            new ListAlbumItemsCommand { AlbumId = albumId, Limit = 50, KindFilter = MediaKind.Video }, default);

        response.Items.Should().ContainSingle().Which.File.Id.Should().Be(video.ToString());
    }
}
