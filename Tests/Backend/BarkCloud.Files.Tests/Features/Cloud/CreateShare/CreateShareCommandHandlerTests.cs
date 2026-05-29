using BarkCloud.Files.Features.Cloud.CreateShare;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DomainShareLink = BarkCloud.Files.Domain.ShareLink;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.CreateShare;

public class CreateShareCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IShareStorage> _storage = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private CreateShareCommandHandler CreateSut() => new(
        _storage.Object, _files.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<CreateShareCommandHandler>.Instance);

    [Fact]
    public async Task Handle_FileNotFound_ThrowsAccessDenied()
    {
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);

        var act = () => CreateSut().Handle(new CreateShareCommand { FileId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _storage.Verify(s => s.Add(It.IsAny<DomainShareLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForeignFile_ThrowsAccessDenied()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { 999 } });

        var act = () => CreateSut().Handle(new CreateShareCommand { FileId = fileId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _storage.Verify(s => s.Add(It.IsAny<DomainShareLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_AddsShareAndReturnsInfo()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });

        var response = await CreateSut().Handle(new CreateShareCommand { FileId = fileId, Name = "Holiday" }, default);

        response.FileId.Should().Be(fileId.ToString());
        response.Name.Should().Be("Holiday");
        response.Token.Should().NotBeNullOrEmpty();
        _storage.Verify(s => s.Add(
            It.Is<DomainShareLink>(x => x.OwnerId == OwnerId && x.FileId == fileId && x.Name == "Holiday"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
