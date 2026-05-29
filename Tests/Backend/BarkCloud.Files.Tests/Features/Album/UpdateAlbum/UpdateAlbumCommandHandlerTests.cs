using BarkCloud.Files.Features.Album.UpdateAlbum;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainAlbum = BarkCloud.Files.Domain.Album;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Album.UpdateAlbum;

public class UpdateAlbumCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IAlbumStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private UpdateAlbumCommandHandler CreateSut()
    {
        var viewBuilder = new AlbumViewBuilder(
            _storage.Object, _files.Object,
            new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty());

        return new UpdateAlbumCommandHandler(
            _storage.Object, _files.Object, viewBuilder,
            UserContextFactory.Create(OwnerId), NullLogger<UpdateAlbumCommandHandler>.Instance);
    }

    private void SetupAlbum(DomainAlbum album) =>
        _storage.Setup(s => s.GetAlbum(album.Id, It.IsAny<CancellationToken>())).ReturnsAsync(album);

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetAlbum(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainAlbum?)null);

        var act = () => CreateSut().Handle(new UpdateAlbumCommand { AlbumId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<AlbumNotFoundException>();
    }

    [Fact]
    public async Task Handle_NotOwner_Throws()
    {
        var album = new DomainAlbum { Id = Guid.NewGuid(), OwnerId = 999 };
        SetupAlbum(album);

        var act = () => CreateSut().Handle(new UpdateAlbumCommand { AlbumId = album.Id, Name = "x" }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_RenameToExistingName_Throws()
    {
        var album = new DomainAlbum { Id = Guid.NewGuid(), OwnerId = OwnerId, Name = "Old" };
        SetupAlbum(album);
        _storage.Setup(s => s.AlbumNameExists(OwnerId, "Taken", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new UpdateAlbumCommand { AlbumId = album.Id, Name = "Taken" }, default);

        await act.Should().ThrowAsync<AlbumNameConflictException>();
    }

    [Fact]
    public async Task Handle_SetCoverToForeignFile_ThrowsAccessDenied()
    {
        var album = new DomainAlbum { Id = Guid.NewGuid(), OwnerId = OwnerId };
        SetupAlbum(album);
        var coverId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(coverId)).ReturnsAsync(new UploadFileEntity { Id = coverId, Uploaders = new() { 999 } });

        var act = () => CreateSut().Handle(
            new UpdateAlbumCommand { AlbumId = album.Id, UpdateCover = true, CoverFileId = coverId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_HappyPath_UpdatesNameAndDescription()
    {
        var album = new DomainAlbum { Id = Guid.NewGuid(), OwnerId = OwnerId, Name = "Old", Description = "old" };
        SetupAlbum(album);
        _storage.Setup(s => s.AlbumNameExists(OwnerId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _storage.Setup(s => s.GetItemCounts(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int> { [album.Id] = 3 });

        var response = await CreateSut().Handle(
            new UpdateAlbumCommand { AlbumId = album.Id, Name = " New ", Description = " desc " }, default);

        response.Name.Should().Be("New");
        response.Description.Should().Be("desc");
        response.ItemsCount.Should().Be(3);
        _storage.Verify(s => s.UpdateAlbum(It.Is<DomainAlbum>(a => a.Name == "New" && a.Description == "desc"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
