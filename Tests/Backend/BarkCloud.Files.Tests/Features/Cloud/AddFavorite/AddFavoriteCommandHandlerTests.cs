using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.AddFavorite;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainFavoriteFile = BarkCloud.Files.Domain.FavoriteFile;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.AddFavorite;

public class AddFavoriteCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IFavoriteFilesStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private AddFavoriteCommandHandler CreateSut() => new(
        _storage.Object, _files.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<AddFavoriteCommandHandler>.Instance);

    [Fact]
    public async Task Handle_FileNotFound_ThrowsAccessDenied()
    {
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);

        var act = () => CreateSut().Handle(new AddFavoriteCommand { FileId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _storage.Verify(s => s.Add(It.IsAny<DomainFavoriteFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForeignFile_ThrowsAccessDenied()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { 999 } });

        var act = () => CreateSut().Handle(new AddFavoriteCommand { FileId = fileId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _storage.Verify(s => s.Add(It.IsAny<DomainFavoriteFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyFavorite_Idempotent_NoAdd()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });
        _storage.Setup(s => s.Exists(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateSut().Handle(new AddFavoriteCommand { FileId = fileId }, default);

        _storage.Verify(s => s.Add(It.IsAny<DomainFavoriteFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_AddsFavorite()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });
        _storage.Setup(s => s.Exists(OwnerId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new AddFavoriteCommand { FileId = fileId }, default);

        _storage.Verify(s => s.Add(
            It.Is<DomainFavoriteFile>(f => f.OwnerId == OwnerId && f.FileId == fileId),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
