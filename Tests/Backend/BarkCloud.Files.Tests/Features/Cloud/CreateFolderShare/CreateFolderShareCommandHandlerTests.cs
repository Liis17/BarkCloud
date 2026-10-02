using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.CreateFolderShare;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.CreateFolderShare;

public class CreateFolderShareCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IFolderShareStorage> _shares = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();
    private readonly Mock<ICloudTreeLock> _treeLock = new();

    public CreateFolderShareCommandHandlerTests()
    {
        _hierarchy.Setup(s => s.LockTree(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync(_treeLock.Object);
    }

    private CreateFolderShareCommandHandler CreateSut() => new(
        _shares.Object,
        _hierarchy.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<CreateFolderShareCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DirectoryNotFound_ThrowsAndDoesNotCommit()
    {
        var dirId = Guid.NewGuid();
        _hierarchy.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new CreateFolderShareCommand { DirectoryId = dirId }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        _treeLock.Verify(l => l.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForeignDirectory_ThrowsAccessDenied()
    {
        var dirId = Guid.NewGuid();
        _hierarchy.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = dirId, OwnerId = 999 });

        var act = () => CreateSut().Handle(new CreateFolderShareCommand { DirectoryId = dirId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_ShareExists_ReturnsExistingWithoutAdding()
    {
        var dirId = Guid.NewGuid();
        _hierarchy.Setup(s => s.GetDirectoryAsNoTracking(dirId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = dirId, OwnerId = OwnerId, Name = "Docs" });
        _shares.Setup(s => s.GetByDirectory(OwnerId, dirId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FolderShareLink { Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = dirId, Token = "t", Name = "Docs" });

        var response = await CreateSut().Handle(new CreateFolderShareCommand { DirectoryId = dirId }, default);

        response.Token.Should().Be("t");
        _shares.Verify(s => s.Add(It.IsAny<FolderShareLink>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .ReturnsAsync(new CloudDirectory { Id = dirId, OwnerId = OwnerId, Name = "Docs" });
        _shares.Setup(s => s.Add(It.IsAny<FolderShareLink>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("add")).Returns(Task.CompletedTask);
        _treeLock.Setup(l => l.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("commit")).Returns(Task.CompletedTask);

        var response = await CreateSut().Handle(new CreateFolderShareCommand { DirectoryId = dirId }, default);

        response.DirectoryId.Should().Be(dirId.ToString());
        calls.Should().Equal("lock", "check", "add", "commit");
    }
}
