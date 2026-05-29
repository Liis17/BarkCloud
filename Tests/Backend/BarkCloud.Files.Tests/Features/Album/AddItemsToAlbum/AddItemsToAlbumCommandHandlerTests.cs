using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Album.AddItemsToAlbum;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainAlbum = BarkCloud.Files.Domain.Album;
using DomainAlbumItem = BarkCloud.Files.Domain.AlbumItem;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Album.AddItemsToAlbum;

public class AddItemsToAlbumCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IAlbumStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private AddItemsToAlbumCommandHandler CreateSut() => new(
        _storage.Object,
        _files.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<AddItemsToAlbumCommandHandler>.Instance);

    private UploadFileEntity OwnedPhoto(Guid id) => new() { Id = id, Uploaders = new() { OwnerId }, MediaKind = MediaKind.Photo };

    [Fact]
    public async Task Handle_AlbumNotFound_Throws()
    {
        _storage.Setup(s => s.GetAlbum(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainAlbum?)null);

        var act = () => CreateSut().Handle(
            new AddItemsToAlbumCommand { AlbumId = Guid.NewGuid(), FileIds = new() { Guid.NewGuid() } }, default);

        await act.Should().ThrowAsync<AlbumNotFoundException>();
    }

    [Fact]
    public async Task Handle_ForeignFileInRequest_ThrowsAccessDenied()
    {
        var albumId = Guid.NewGuid();
        var ownFile = Guid.NewGuid();
        var foreignFile = Guid.NewGuid();
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainAlbum { Id = albumId, OwnerId = OwnerId });
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            OwnedPhoto(ownFile),
            new() { Id = foreignFile, Uploaders = new() { 999 }, MediaKind = MediaKind.Photo }
        });

        var act = () => CreateSut().Handle(
            new AddItemsToAlbumCommand { AlbumId = albumId, FileIds = new() { ownFile, foreignFile } }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _storage.Verify(s => s.AddItems(It.IsAny<IEnumerable<DomainAlbumItem>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_AddsNewItemsAndSetsCover()
    {
        var albumId = Guid.NewGuid();
        var f1 = Guid.NewGuid();
        var f2 = Guid.NewGuid();
        var album = new DomainAlbum { Id = albumId, OwnerId = OwnerId, CoverFileId = null };
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(album);
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<UploadFileEntity> { OwnedPhoto(f1), OwnedPhoto(f2) });
        _storage.Setup(s => s.GetExistingItemFileIds(albumId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { f1 });

        List<DomainAlbumItem>? added = null;
        _storage.Setup(s => s.AddItems(It.IsAny<IEnumerable<DomainAlbumItem>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<DomainAlbumItem>, CancellationToken>((items, _) => added = items.ToList())
            .Returns(Task.CompletedTask);

        await CreateSut().Handle(new AddItemsToAlbumCommand { AlbumId = albumId, FileIds = new() { f1, f2 } }, default);

        added.Should().ContainSingle().Which.FileId.Should().Be(f2);
        _storage.Verify(s => s.UpdateAlbum(It.Is<DomainAlbum>(a => a.CoverFileId == f2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AllAlreadyPresent_NoAddNoCover()
    {
        var albumId = Guid.NewGuid();
        var f1 = Guid.NewGuid();
        var album = new DomainAlbum { Id = albumId, OwnerId = OwnerId, CoverFileId = null };
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(album);
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity> { OwnedPhoto(f1) });
        _storage.Setup(s => s.GetExistingItemFileIds(albumId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { f1 });

        await CreateSut().Handle(new AddItemsToAlbumCommand { AlbumId = albumId, FileIds = new() { f1 } }, default);

        _storage.Verify(s => s.AddItems(It.IsAny<IEnumerable<DomainAlbumItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        _storage.Verify(s => s.UpdateAlbum(It.IsAny<DomainAlbum>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
