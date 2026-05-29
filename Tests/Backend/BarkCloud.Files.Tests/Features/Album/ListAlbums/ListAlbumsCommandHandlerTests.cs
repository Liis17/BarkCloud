using BarkCloud.Files.Features.Album.ListAlbums;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using DomainAlbum = BarkCloud.Files.Domain.Album;

namespace BarkCloud.Files.Tests.Features.Album.ListAlbums;

public class ListAlbumsCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IAlbumStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private ListAlbumsCommandHandler CreateSut()
    {
        var viewBuilder = new AlbumViewBuilder(
            _storage.Object, _files.Object,
            new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty());

        return new ListAlbumsCommandHandler(
            _storage.Object, viewBuilder,
            UserContextFactory.Create(OwnerId), NullLogger<ListAlbumsCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_Empty_ReturnsEmpty()
    {
        _storage.Setup(s => s.ListAlbumsPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainAlbum>());

        var response = await CreateSut().Handle(new ListAlbumsCommand { Limit = 50 }, default);

        response.Albums.Should().BeEmpty();
        response.NextCursorAlbumId.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_MoreThanLimit_TrimsAndSetsCursor()
    {
        // limit=2, возвращаем 3 (limit+1) → hasMore, последний обрезается и становится курсором.
        var albums = Enumerable.Range(0, 3)
            .Select(i => new DomainAlbum { Id = Guid.NewGuid(), OwnerId = OwnerId, Name = $"A{i}", UpdatedAt = DateTime.UtcNow.AddMinutes(-i) })
            .ToList();
        _storage.Setup(s => s.ListAlbumsPage(OwnerId, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(albums);
        _storage.Setup(s => s.GetItemCounts(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int>());

        var response = await CreateSut().Handle(new ListAlbumsCommand { Limit = 2 }, default);

        response.Albums.Should().HaveCount(2);
        response.NextCursorAlbumId.Should().Be(albums[1].Id.ToString());
    }
}
