using BarkCloud.Files.Features.Album.DeleteAlbum;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainAlbum = BarkCloud.Files.Domain.Album;

namespace BarkCloud.Files.Tests.Features.Album.DeleteAlbum;

public class DeleteAlbumCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IAlbumStorage> _storage = new();

    private DeleteAlbumCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<DeleteAlbumCommandHandler>.Instance);

    [Fact]
    public async Task Handle_AlbumNotFound_Throws()
    {
        _storage.Setup(s => s.GetAlbum(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainAlbum?)null);

        var act = () => CreateSut().Handle(new DeleteAlbumCommand { AlbumId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<AlbumNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_Throws()
    {
        var albumId = Guid.NewGuid();
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainAlbum { Id = albumId, OwnerId = 999 });

        var act = () => CreateSut().Handle(new DeleteAlbumCommand { AlbumId = albumId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _storage.Verify(s => s.RemoveAlbum(It.IsAny<DomainAlbum>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_RemovesAlbum()
    {
        var albumId = Guid.NewGuid();
        var album = new DomainAlbum { Id = albumId, OwnerId = OwnerId };
        _storage.Setup(s => s.GetAlbum(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(album);

        await CreateSut().Handle(new DeleteAlbumCommand { AlbumId = albumId }, default);

        _storage.Verify(s => s.RemoveAlbum(album, It.IsAny<CancellationToken>()), Times.Once);
    }
}
