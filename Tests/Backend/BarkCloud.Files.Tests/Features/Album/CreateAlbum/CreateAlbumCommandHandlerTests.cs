using BarkCloud.Files.Features.Album.CreateAlbum;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainAlbum = BarkCloud.Files.Domain.Album;

namespace BarkCloud.Files.Tests.Features.Album.CreateAlbum;

public class CreateAlbumCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IAlbumStorage> _storage = new();

    private CreateAlbumCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<CreateAlbumCommandHandler>.Instance);

    [Fact]
    public async Task Handle_EmptyName_Throws()
    {
        var act = () => CreateSut().Handle(new CreateAlbumCommand { Name = "   " }, default);

        await act.Should().ThrowAsync<AlbumNameConflictException>();
        _storage.Verify(s => s.AddAlbum(It.IsAny<DomainAlbum>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NameAlreadyExists_Throws()
    {
        _storage.Setup(s => s.AlbumNameExists(OwnerId, "Trips", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new CreateAlbumCommand { Name = "Trips" }, default);

        await act.Should().ThrowAsync<AlbumNameConflictException>();
        _storage.Verify(s => s.AddAlbum(It.IsAny<DomainAlbum>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_AddsAlbumAndReturnsInfo()
    {
        _storage.Setup(s => s.AlbumNameExists(OwnerId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var response = await CreateSut().Handle(new CreateAlbumCommand { Name = "  Trips  ", Description = " sea " }, default);

        response.Name.Should().Be("Trips");
        response.Description.Should().Be("sea");
        response.ItemsCount.Should().Be(0);
        _storage.Verify(s => s.AddAlbum(
            It.Is<DomainAlbum>(a => a.OwnerId == OwnerId && a.Name == "Trips" && a.Description == "sea"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
