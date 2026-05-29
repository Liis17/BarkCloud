using BarkCloud.Files.Features.Cloud.RemoveFavorite;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.Cloud.RemoveFavorite;

public class RemoveFavoriteCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IFavoriteFilesStorage> _storage = new();

    private RemoveFavoriteCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<RemoveFavoriteCommandHandler>.Instance);

    [Fact]
    public async Task Handle_RemovesOwnerRow()
    {
        var fileId = Guid.NewGuid();
        _storage.Setup(s => s.Remove(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await CreateSut().Handle(new RemoveFavoriteCommand { FileId = fileId }, default);

        _storage.Verify(s => s.Remove(OwnerId, fileId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NotFavorite_NoError()
    {
        var fileId = Guid.NewGuid();
        _storage.Setup(s => s.Remove(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var act = () => CreateSut().Handle(new RemoveFavoriteCommand { FileId = fileId }, default);

        await act.Should().NotThrowAsync();
    }
}
