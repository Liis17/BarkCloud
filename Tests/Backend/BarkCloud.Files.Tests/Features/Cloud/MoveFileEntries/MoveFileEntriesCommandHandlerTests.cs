using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.MoveFileEntries;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;
using DomainFileEntry = BarkCloud.Files.Domain.CloudFileEntry;

namespace BarkCloud.Files.Tests.Features.Cloud.MoveFileEntries;

public class MoveFileEntriesCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<ICloudHierarchyStorage> _storage = new();
    private readonly Mock<ICloudTreeLock> _treeLock = new();
    private readonly Mock<IFileActivityStorage> _activity = new();
    private readonly Guid _source = Guid.NewGuid();
    private readonly Guid _target = Guid.NewGuid();

    public MoveFileEntriesCommandHandlerTests()
    {
        _storage.Setup(s => s.LockTree(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync(_treeLock.Object);
        _storage.Setup(s => s.GetDirectoryAsNoTracking(_target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = _target, OwnerId = OwnerId, Name = "картинки" });
    }

    private MoveFileEntriesCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<MoveFileEntriesCommandHandler>.Instance,
        new FileActivityWriter(_activity.Object, NullLogger<FileActivityWriter>.Instance));

    private DomainFileEntry Entry(string name, Guid? directoryId = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = Guid.NewGuid(), Name = name,
        DirectoryId = directoryId ?? _source,
    };

    private void SetupEntries(IReadOnlyCollection<DomainFileEntry> entries, params string[] takenNames)
    {
        _storage.Setup(s => s.GetLiveFileEntriesByIds(OwnerId, It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries.ToList());
        _storage.Setup(s => s.GetLiveFileEntryNames(OwnerId, _target, It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(takenNames.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Handle_MovesEntriesAndReportsPerEntryFailures()
    {
        var ok = Entry("a.jpg");
        var conflict = Entry("taken.jpg");
        var duplicateInBatch = Entry("a.jpg");
        var alreadyThere = Entry("b.jpg", _target);
        var missing = Guid.NewGuid();
        SetupEntries([ok, conflict, duplicateInBatch, alreadyThere], "taken.jpg");

        var response = await CreateSut().Handle(new MoveFileEntriesCommand
        {
            EntryIds = [ok.Id, conflict.Id, duplicateInBatch.Id, alreadyThere.Id, missing],
            NewDirectoryId = _target,
        }, default);

        response.MovedEntryIds.Should().BeEquivalentTo([ok.Id.ToString(), alreadyThere.Id.ToString()]);
        response.Failed.Select(f => (f.EntryId, f.ErrorCode)).Should().BeEquivalentTo([
            (conflict.Id.ToString(), new DirectoryNameConflictException().ErrorCode),
            (duplicateInBatch.Id.ToString(), new DirectoryNameConflictException().ErrorCode),
            (missing.ToString(), new FileEntryNotFoundException().ErrorCode),
        ]);
        ok.DirectoryId.Should().Be(_target);
        conflict.DirectoryId.Should().Be(_source);
        duplicateInBatch.DirectoryId.Should().Be(_source);
        _storage.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _treeLock.Verify(l => l.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_TargetDirectoryNotFound_RejectsWholeBatch()
    {
        var missingDirectory = Guid.NewGuid();
        var entry = Entry("a.jpg");
        SetupEntries([entry]);

        var act = () => CreateSut().Handle(new MoveFileEntriesCommand
        {
            EntryIds = [entry.Id], NewDirectoryId = missingDirectory,
        }, default);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        entry.DirectoryId.Should().Be(_source);
        _treeLock.Verify(l => l.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForeignTargetDirectory_ThrowsAccessDenied()
    {
        var foreign = Guid.NewGuid();
        _storage.Setup(s => s.GetDirectoryAsNoTracking(foreign, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudDirectory { Id = foreign, OwnerId = 999, Name = "чужая" });

        var act = () => CreateSut().Handle(new MoveFileEntriesCommand
        {
            EntryIds = [Guid.NewGuid()], NewDirectoryId = foreign,
        }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _treeLock.Verify(l => l.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
