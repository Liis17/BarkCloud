using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.DeleteFileEntry;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.DeleteFileEntry;

public class DeleteFileEntryCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<ICloudTreeLock> _treeLock = new();
    private readonly Mock<IFileActivityStorage> _activity = new();

    public DeleteFileEntryCommandHandlerTests()
    {
        _storage.Setup(s => s.LockTree(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync(_treeLock.Object);
    }

    private DeleteFileEntryCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<DeleteFileEntryCommandHandler>.Instance,
        new FileActivityWriter(_activity.Object, NullLogger<FileActivityWriter>.Instance));

    [Fact]
    public async Task Handle_NotFound_Throws()
    {
        _storage.Setup(s => s.GetFileEntry(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DomainFileEntry?)null);

        var act = () => CreateSut().Handle(new DeleteFileEntryCommand { EntryId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_AlreadyDeleted_Throws()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = OwnerId, IsDeleted = true });

        var act = () => CreateSut().Handle(new DeleteFileEntryCommand { EntryId = id }, default);

        await act.Should().ThrowAsync<FileEntryNotFoundException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_NotOwner_ThrowsAccessDenied()
    {
        var id = Guid.NewGuid();
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFileEntry { Id = id, OwnerId = 999 });

        var act = () => CreateSut().Handle(new DeleteFileEntryCommand { EntryId = id }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        AssertNoCommitOrActivity();
    }

    [Fact]
    public async Task Handle_HappyPath_SoftDeletesAndSaves()
    {
        var id = Guid.NewGuid();
        var entry = new DomainFileEntry { Id = id, OwnerId = OwnerId, FileId = Guid.NewGuid() };
        _storage.Setup(s => s.GetFileEntry(id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);

        await CreateSut().Handle(new DeleteFileEntryCommand { EntryId = id }, default);

        entry.IsDeleted.Should().BeTrue();
        entry.DeletedAt.Should().NotBeNull();
        entry.PurgeAt.Should().NotBeNull();
        _storage.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_HappyPath_CommitsBeforeActivityAndDisposesLock()
    {
        var calls = new List<string>();
        var entry = new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId };
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _storage.Setup(s => s.LockTree(OwnerId, ct))
            .Callback(() => calls.Add("lock")).ReturnsAsync(_treeLock.Object);
        _storage.Setup(s => s.GetFileEntry(entry.Id, ct))
            .Callback(() => calls.Add("read")).ReturnsAsync(entry);
        _storage.Setup(s => s.SaveChangesAsync(ct)).Callback(() => calls.Add("save")).Returns(Task.CompletedTask);
        _treeLock.Setup(l => l.CommitAsync(ct)).Callback(() => calls.Add("commit")).Returns(Task.CompletedTask);
        _activity.Setup(s => s.AddRange(It.IsAny<IEnumerable<FileActivityEvent>>(), ct))
            .Callback(() => calls.Add("activity")).Returns(Task.CompletedTask);
        _treeLock.Setup(l => l.DisposeAsync()).Callback(() => calls.Add("dispose")).Returns(ValueTask.CompletedTask);

        await CreateSut().Handle(new DeleteFileEntryCommand { EntryId = entry.Id }, ct);

        calls.Should().Equal("lock", "read", "save", "commit", "activity", "dispose");
    }

    [Fact]
    public async Task Handle_RowDisappears_DoesNotCommitOrWriteActivity()
    {
        var entry = new DomainFileEntry { Id = Guid.NewGuid(), OwnerId = OwnerId };
        _storage.Setup(s => s.GetFileEntry(entry.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _storage.Setup(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException());

        var act = () => CreateSut().Handle(new DeleteFileEntryCommand { EntryId = entry.Id }, default);

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
