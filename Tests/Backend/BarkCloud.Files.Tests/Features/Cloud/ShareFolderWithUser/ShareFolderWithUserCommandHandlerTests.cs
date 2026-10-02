using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ShareFolderWithUser;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.ShareFolderWithUser;

public class ShareFolderWithUserCommandHandlerTests
{
    private const long OwnerId = 42;
    private const long RecipientId = 7;
    private readonly Mock<IDirectoryGrantStorage> _grants = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();
    private readonly Mock<ICloudTreeLock> _treeLock = new();

    public ShareFolderWithUserCommandHandlerTests()
    {
        _hierarchy.Setup(s => s.LockTree(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync(_treeLock.Object);
    }

    private ShareFolderWithUserCommandHandler CreateSut() => new(
        _grants.Object,
        _hierarchy.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<ShareFolderWithUserCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DirectoryNotFound_ThrowsAndDoesNotCommit()
    {
        var dirId = Guid.NewGuid();
        _hierarchy.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new ShareFolderWithUserCommand { DirectoryId = dirId, RecipientUserId = RecipientId }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        _treeLock.Verify(l => l.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForeignDirectory_ThrowsAccessDenied()
    {
        var dirId = Guid.NewGuid();
        _hierarchy.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = dirId, OwnerId = 999 });

        var act = () => CreateSut().Handle(new ShareFolderWithUserCommand { DirectoryId = dirId, RecipientUserId = RecipientId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_GrantExists_DoesNotAdd()
    {
        var dirId = Guid.NewGuid();
        _hierarchy.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = dirId, OwnerId = OwnerId });
        _grants.Setup(s => s.Exists(OwnerId, dirId, RecipientId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateSut().Handle(new ShareFolderWithUserCommand { DirectoryId = dirId, RecipientUserId = RecipientId }, default);

        _grants.Verify(s => s.Add(It.IsAny<DirectoryGrant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_LocksTreeBeforeCheckingDirectoryAndCommitsAfterAdd()
    {
        var dirId = Guid.NewGuid();
        var calls = new List<string>();
        _hierarchy.Setup(s => s.LockTree(OwnerId, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("lock")).ReturnsAsync(_treeLock.Object);
        _hierarchy.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("check"))
            .ReturnsAsync(new CloudDirectory { Id = dirId, OwnerId = OwnerId });
        _grants.Setup(s => s.Add(It.IsAny<DirectoryGrant>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("add")).Returns(Task.CompletedTask);
        _treeLock.Setup(l => l.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("commit")).Returns(Task.CompletedTask);

        await CreateSut().Handle(new ShareFolderWithUserCommand { DirectoryId = dirId, RecipientUserId = RecipientId }, default);

        calls.Should().Equal("lock", "check", "add", "commit");
    }
}
