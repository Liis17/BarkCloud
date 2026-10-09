using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.RenameFileEntry;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.RenameFileEntry;

public class RenameFileEntryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<ICloudTreeLock> _treeLock = new();
    private readonly Mock<IFileActivityStorage> _activity = new();

    public RenameFileEntryCommandHandlerTests()
    {
        _storage.Setup(s => s.LockTree(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync(_treeLock.Object);
    }

    private RenameFileEntryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<RenameFileEntryCommandHandler>.Instance,
        new FileActivityWriter(_activity.Object, NullLogger<FileActivityWriter>.Instance));

    [Fact]
    public async Task Handle_EmptyName_Throws()
    {
        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = Guid.NewGuid(), NewName = " " }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
        _storage.Verify(s => s.LockTree(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetFileEntry(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainFileEntry?)null);

        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = Guid.NewGuid(), NewName = "x" }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = "x" }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_SameName_NoUpdate()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, Name = "Same" });

        await CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = "Same" }, default);

        _storage.Verify(s => s.UpdateFileEntry(It.IsAny<DomainFileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_NameConflict_Throws()
    {
        var id = Guid.NewGuid();
        var dirId = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = dirId, Name = "Old" });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, dirId, "Taken", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = "Taken" }, default);

        await act.Should().ThrowAsync<DirectoryNameConflictException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_HappyPath_Updates()
    {
        var id = Guid.NewGuid();
        var dirId = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, DirectoryId = dirId, Name = "Old" });
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, dirId, "New", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = " New " }, default);

        _storage.Verify(s => s.UpdateFileEntry(It.Is<DomainFileEntry>(e => e.Name == "New"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Same")]
    [InlineData("New")]
    public async Task Handle_TrashedEntry_ThrowsEvenForSameName(string newName)
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, Name = "Same", IsDeleted = true });

        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = id, NewName = newName }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        _storage.Verify(s => s.FileEntryNameExists(It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _storage.Verify(s => s.UpdateFileEntry(It.IsAny<DomainFileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_HappyPath_CommitsBeforeActivityAndDisposesLock()
    {
        var calls = new List<string>();
        var entry = new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId, Name = "Old" };
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _storage.Setup(s => s.LockTree(OwnerId, ct))
            .Callback(() => calls.Add("lock")).ReturnsAsync(_treeLock.Object);
        _storage.Setup(s => s.GetFileEntry(entry.Id, ct))
            .Callback(() => calls.Add("read")).ReturnsAsync(entry);
        _storage.Setup(s => s.FileEntryNameExists(OwnerId, entry.DirectoryId, "New", ct))
            .Callback(() => calls.Add("check")).ReturnsAsync(false);
        _storage.Setup(s => s.UpdateFileEntry(entry, ct))
            .Callback(() => calls.Add("save")).Returns(Task.CompletedTask);
        _treeLock.Setup(l => l.CommitAsync(ct)).Callback(() => calls.Add("commit")).Returns(Task.CompletedTask);
        _activity.Setup(s => s.AddRange(It.IsAny<IEnumerable<FileActivityEvent>>(), ct))
            .Callback(() => calls.Add("activity")).Returns(Task.CompletedTask);
        _treeLock.Setup(l => l.DisposeAsync()).Callback(() => calls.Add("dispose")).Returns(ValueTask.CompletedTask);

        await CreateSut().Handle(new RenameFileEntryCommand { EntryId = entry.Id, NewName = "New" }, ct);

        calls.Should().Equal("lock", "read", "check", "save", "commit", "activity", "dispose");
    }

    [Fact]
    public async Task Handle_RowDisappears_DoesNotCommitOrWriteActivity()
    {
        var entry = new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId, Name = "Old" };
        _storage.Setup(s => s.GetFileEntry(entry.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _storage.Setup(s => s.UpdateFileEntry(entry, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException());

        var act = () => CreateSut().Handle(new RenameFileEntryCommand { EntryId = entry.Id, NewName = "New" }, default);

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
