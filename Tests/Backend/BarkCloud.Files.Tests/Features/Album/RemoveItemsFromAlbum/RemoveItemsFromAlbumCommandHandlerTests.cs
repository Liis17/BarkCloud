using BarkCloud.Files.Features.Album.RemoveItemsFromAlbum;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainAlbum = BarkCloud.Files.Domain.Album;
using DomainAlbumItem = BarkCloud.Files.Domain.AlbumItem;

namespace BarkCloud.Files.Tests.Features.Album.RemoveItemsFromAlbum;

public class RemoveItemsFromAlbumCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IAlbumStorage> _storage = new();

    private RemoveItemsFromAlbumCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<RemoveItemsFromAlbumCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NotOwner_Throws()
    {
        var albumId = Guid.NewGuid();
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainAlbum { Id = albumId, OwnerId = 999 });

        var act = () => CreateSut().Handle(
            new RemoveItemsFromAlbumCommand { AlbumId = albumId, FileIds = new() { Guid.NewGuid() } }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_EmptyFileIds_NoRemoval()
    {
        var albumId = Guid.NewGuid();
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainAlbum { Id = albumId, OwnerId = OwnerId });

        await CreateSut().Handle(new RemoveItemsFromAlbumCommand { AlbumId = albumId, FileIds = new() }, default);

        _storage.Verify(s => s.RemoveItems(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RemovingCurrentCover_ResetsCoverToFirstRemaining()
    {
        var albumId = Guid.NewGuid();
        var coverId = Guid.NewGuid();
        var nextFileId = Guid.NewGuid();
        var album = new DomainAlbum { Id = albumId, OwnerId = OwnerId, CoverFileId = coverId };
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(album);
        _storage.Setup(s => s.RemoveItems(albumId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _storage.Setup(s => s.GetFirstItem(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainAlbumItem { AlbumId = albumId, FileId = nextFileId });

        await CreateSut().Handle(new RemoveItemsFromAlbumCommand { AlbumId = albumId, FileIds = new() { coverId } }, default);

        _storage.Verify(s => s.UpdateAlbum(It.Is<DomainAlbum>(a => a.CoverFileId == nextFileId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RemovingNonCover_DoesNotTouchCover()
    {
        var albumId = Guid.NewGuid();
        var coverId = Guid.NewGuid();
        var album = new DomainAlbum { Id = albumId, OwnerId = OwnerId, CoverFileId = coverId };
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(album);
        _storage.Setup(s => s.RemoveItems(albumId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await CreateSut().Handle(new RemoveItemsFromAlbumCommand { AlbumId = albumId, FileIds = new() { Guid.NewGuid() } }, default);

        _storage.Verify(s => s.UpdateAlbum(It.IsAny<DomainAlbum>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
