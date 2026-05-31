using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ShareFileWithUser;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ShareFileWithUser;

public class ShareFileWithUserCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IGrantStorage> _grants = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private ShareFileWithUserCommandHandler CreateSut() => new(
        _grants.Object, _files.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<ShareFileWithUserCommandHandler>.Instance);

    [Fact]
    public async Task Handle_ForeignFile_ThrowsAccessDenied()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { 999 } });

        var act = () => CreateSut().Handle(new ShareFileWithUserCommand { FileId = fileId, RecipientUserId = 7 }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _grants.Verify(s => s.Add(It.IsAny<FileGrant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ShareWithSelf_NoOp()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });

        await CreateSut().Handle(new ShareFileWithUserCommand { FileId = fileId, RecipientUserId = OwnerId }, default);

        _grants.Verify(s => s.Add(It.IsAny<FileGrant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyShared_Idempotent()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });
        _grants.Setup(s => s.Exists(OwnerId, fileId, 7, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateSut().Handle(new ShareFileWithUserCommand { FileId = fileId, RecipientUserId = 7 }, default);

        _grants.Verify(s => s.Add(It.IsAny<FileGrant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_AddsGrant()
    {
        var fileId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId, Uploaders = new() { OwnerId } });
        _grants.Setup(s => s.Exists(OwnerId, fileId, 7, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new ShareFileWithUserCommand { FileId = fileId, RecipientUserId = 7 }, default);

        _grants.Verify(s => s.Add(
            It.Is<FileGrant>(g => g.OwnerId == OwnerId && g.RecipientId == 7 && g.FileId == fileId),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
