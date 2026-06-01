using BarkCloud.Files.Features.Cloud.DeleteUserMedia;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.DeleteUserMedia;

public class DeleteUserMediaCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<IAlbumStorage> _albums = new();

    private DeleteUserMediaCommandHandler CreateSut() => new(
        _hierarchy.Object, _files.Object, _albums.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<DeleteUserMediaCommandHandler>.Instance);

    [Fact]
    public async Task Handle_HasLiveEntries_SoftDeletesThem()
    {
        var fileId = Guid.NewGuid();
        var entries = new List<DomainFileEntry>
        {
            new() { Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId },
        };
        _hierarchy.Setup(s => s.GetLiveEntriesForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(entries);

        await CreateSut().Handle(new DeleteUserMediaCommand { FileId = fileId }, default);

        entries[0].IsDeleted.Should().BeTrue();
        entries[0].DeletedAt.Should().NotBeNull();
        entries[0].PurgeAt.Should().NotBeNull();
        _hierarchy.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _files.Verify(s => s.RemoveUploaderFromFile(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        // Мягкое удаление (в корзину) НЕ трогает альбомы: членство сохраняется для восстановления.
        _albums.Verify(s => s.RemoveFileFromAllAlbums(It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NoEntries_RemovesUploaderFromBlob()
    {
        var fileId = Guid.NewGuid();
        _hierarchy.Setup(s => s.GetLiveEntriesForFile(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<DomainFileEntry>());

        await CreateSut().Handle(new DeleteUserMediaCommand { FileId = fileId }, default);

        _files.Verify(s => s.RemoveUploaderFromFile(fileId, OwnerId, It.IsAny<CancellationToken>()), Times.Once);
        _hierarchy.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        // Жёсткое удаление обязано вычистить файл из всех альбомов владельца (фикс бага-сироты).
        _albums.Verify(s => s.RemoveFileFromAllAlbums(OwnerId, fileId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
