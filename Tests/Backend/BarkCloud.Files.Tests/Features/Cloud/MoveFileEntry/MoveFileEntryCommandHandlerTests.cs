using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.MoveFileEntry;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.MoveFileEntry;

public class MoveFileEntryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<ICloudTreeLock> _treeLock = new();
    private readonly Mock<IFileActivityStorage> _activity = new();

    public MoveFileEntryCommandHandlerTests()
    {
        _storage.Setup(s => s.LockTree(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync(_treeLock.Object);
    }

    private MoveFileEntryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<MoveFileEntryCommandHandler>.Instance,
        new FileActivityWriter(_activity.Object, NullLogger<FileActivityWriter>.Instance));

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetFileEntry(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainFileEntry?)null);

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_NewDirectoryNotFound_Throws()
    {
        var id = Guid.NewGuid();
        var newDir = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newDir, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = newDir }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_SameDirectory_NoUpdate()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId });

        await CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = null }, default);

        _storage.Verify(s => s.UpdateFileEntry(It.IsAny<DomainFileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_NameConflictInTarget_Throws()
    {
        var id = Guid.NewGuid();
        var newDir = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId, Name = "f.jpg" });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = newDir, OwnerId = OwnerId });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, newDir, "f.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = newDir }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_HappyPath_Updates()
    {
        var id = Guid.NewGuid();
        var newDir = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId, Name = "f.jpg" });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = newDir, OwnerId = OwnerId });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, newDir, "f.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = newDir }, default);

        _storage.Verify(s => s.UpdateFileEntry(It.Is<DomainFileEntry>(e => e.DirectoryId == newDir), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_HappyPath_LocksTreeBeforeReadingAndCommitsAfterUpdate()
    {
        var id = Guid.NewGuid();
        var newDir = Guid.NewGuid();
        var calls = new List<string>();
        _storage.Setup(s => s.LockTree(OwnerId, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("lock")).ReturnsAsync(_treeLock.Object);
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("read"))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId, Name = "f.jpg" });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newDir, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("check"))
            .ReturnsAsync(new CloudDirectory { Id = newDir, OwnerId = OwnerId });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, newDir, "f.jpg", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("name")).ReturnsAsync(false);
        _storage.Setup(s => s.UpdateFileEntry(It.IsAny<DomainFileEntry>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("update")).Returns(Task.CompletedTask);
        _treeLock.Setup(l => l.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("commit")).Returns(Task.CompletedTask);
        _activity.Setup(s => s.AddRange(It.IsAny<IEnumerable<FileActivityEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("activity")).Returns(Task.CompletedTask);
        _treeLock.Setup(l => l.DisposeAsync()).Callback(() => calls.Add("dispose")).Returns(ValueTask.CompletedTask);

        await CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = newDir }, default);

        calls.Should().Equal("lock", "read", "check", "name", "update", "commit", "activity", "dispose");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_TrashedEntry_ThrowsBeforeCheckingTargetOrReturningNoop(bool sameDirectory)
    {
        var id = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry
            {
                Id = id, OwnerId = OwnerId, IsDeleted = true, DirectoryId = CloudHierarchyStorage.RootDirectoryId,
            });

        var act = () => CreateSut().Handle(
            new MoveFileEntryCommand { EntryId = id, NewDirectoryId = sameDirectory ? null : targetId }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        _storage.Verify(s => s.GetDirectoryAsNoTracking(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _storage.Verify(s => s.UpdateFileEntry(It.IsAny<DomainFileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_NewDirectoryNotFound_DoesNotCommit()
    {
        var id = Guid.NewGuid();
        var newDir = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = CloudHierarchyStorage.RootDirectoryId });
        _storage.Setup(s => s.GetDirectoryAsNoTracking(newDir, It.IsAny<CancellationToken>())).ReturnsAsync((CloudDirectory?)null);

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = id, NewDirectoryId = newDir }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_RowDisappears_DoesNotCommitOrWriteActivity()
    {
        var entry = new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = Guid.NewGuid() };
        _storage.Setup(s => s.GetFileEntry(entry.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _storage.Setup(s => s.UpdateFileEntry(entry, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException());

        var act = () => CreateSut().Handle(new MoveFileEntryCommand { EntryId = entry.Id, NewDirectoryId = null }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        AssertNoCommitOrActivity();
    }

    private void AssertNoCommitOrActivity()
    {
        _treeLock.Verify(l => l.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        _treeLock.Verify(l => l.DisposeAsync(), Times.Once);
        _activity.Verify(s => s.AddRange(It.IsAny<IEnumerable<FileActivityEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
