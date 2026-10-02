using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class GrantStorageTests : IDisposable
{
    private const long OwnerId = 7;
    private const long CoOwnerId = 8;
    private const long RecipientId = 42;
    private readonly SqliteFilesContext _database = new();

    [Fact]
    public async Task RecipientHasAccess_LiveEntry_ReturnsTrue()
    {
        var fileId = Guid.NewGuid();
        await Seed(Grant(OwnerId, fileId), Entry(OwnerId, fileId));

        (await Storage().RecipientHasAccess(RecipientId, fileId)).Should().BeTrue();
    }

    [Fact]
    public async Task RecipientHasAccess_OwnerEntryInTrash_ReturnsFalse()
    {
        var fileId = Guid.NewGuid();
        await Seed(Grant(OwnerId, fileId), Entry(OwnerId, fileId, isDeleted: true));

        (await Storage().RecipientHasAccess(RecipientId, fileId)).Should().BeFalse();
    }

    [Fact]
    public async Task RecipientHasAccess_EntryRestoredFromTrash_ReturnsTrueAgain()
    {
        var fileId = Guid.NewGuid();
        var entry = Entry(OwnerId, fileId, isDeleted: true);
        await Seed(Grant(OwnerId, fileId), entry);
        (await Storage().RecipientHasAccess(RecipientId, fileId)).Should().BeFalse();

        entry.IsDeleted = false;
        await _database.Context.SaveChangesAsync();

        (await Storage().RecipientHasAccess(RecipientId, fileId)).Should().BeTrue();
    }

    [Fact]
    public async Task RecipientHasAccess_FileWithoutAnyEntry_ReturnsTrue()
    {
        var fileId = Guid.NewGuid();
        await Seed(Grant(OwnerId, fileId));

        (await Storage().RecipientHasAccess(RecipientId, fileId)).Should().BeTrue();
    }

    [Fact]
    public async Task RecipientHasAccess_OneEntryInTrashAndOneLive_ReturnsTrue()
    {
        var fileId = Guid.NewGuid();
        await Seed(
            Grant(OwnerId, fileId),
            Entry(OwnerId, fileId, isDeleted: true, name: "old.txt"),
            Entry(OwnerId, fileId, name: "new.txt"));

        (await Storage().RecipientHasAccess(RecipientId, fileId)).Should().BeTrue();
    }

    [Fact]
    public async Task RecipientHasAccess_GrantorEntryInTrashButCoOwnerEntryLive_ReturnsFalse()
    {
        var fileId = Guid.NewGuid();
        await Seed(
            Grant(OwnerId, fileId),
            Entry(OwnerId, fileId, isDeleted: true),
            Entry(CoOwnerId, fileId));

        (await Storage().RecipientHasAccess(RecipientId, fileId)).Should().BeFalse();
    }

    [Fact]
    public async Task RecipientHasAccess_AnotherRecipient_ReturnsFalse()
    {
        var fileId = Guid.NewGuid();
        await Seed(Grant(OwnerId, fileId), Entry(OwnerId, fileId));

        (await Storage().RecipientHasAccess(RecipientId + 1, fileId)).Should().BeFalse();
    }

    [Fact]
    public async Task ListSharedWithMePage_SkipsFilesInGrantorTrash()
    {
        var live = Guid.NewGuid();
        var trashed = Guid.NewGuid();
        var withoutEntry = Guid.NewGuid();
        await Seed(
            Grant(OwnerId, live), Entry(OwnerId, live),
            Grant(OwnerId, trashed), Entry(OwnerId, trashed, isDeleted: true),
            Grant(OwnerId, withoutEntry));

        var page = await Storage().ListSharedWithMePage(RecipientId, null, null, limit: 10);

        page.Select(x => x.FileId).Should().BeEquivalentTo(new[] { live, withoutEntry });
    }

    [Fact]
    public async Task ListSharedWithMePage_FiltersBeforeLimit_KeepsPageFull()
    {
        var baseTime = DateTime.UtcNow;
        var fileIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var newestTrashed = Grant(OwnerId, fileIds[0], createdAt: baseTime);
        await Seed(
            newestTrashed, Entry(OwnerId, fileIds[0], isDeleted: true),
            Grant(OwnerId, fileIds[1], createdAt: baseTime.AddMinutes(-1)),
            Grant(OwnerId, fileIds[2], createdAt: baseTime.AddMinutes(-2)),
            Grant(OwnerId, fileIds[3], createdAt: baseTime.AddMinutes(-3)));

        var page = await Storage().ListSharedWithMePage(RecipientId, null, null, limit: 2);

        // limit+1 элемент для признака следующей страницы: корзина не «съедает» место на странице.
        page.Select(x => x.FileId).Should().Equal(fileIds[1], fileIds[2], fileIds[3]);
    }

    [Fact]
    public async Task ListByOwnerPage_StillListsGrantsOfFilesInOwnTrash()
    {
        var fileId = Guid.NewGuid();
        await Seed(Grant(OwnerId, fileId), Entry(OwnerId, fileId, isDeleted: true));

        var page = await Storage().ListByOwnerPage(OwnerId, null, null, limit: 10);

        page.Select(x => x.FileId).Should().Equal(fileId);
    }

    private GrantStorage Storage() => new(_database.Context);

    private async Task Seed(params object[] entities)
    {
        _database.Context.AddRange(entities);
        await _database.Context.SaveChangesAsync();
    }

    private static FileGrant Grant(long ownerId, Guid fileId, long recipientId = RecipientId, DateTime? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = ownerId,
        RecipientId = recipientId,
        FileId = fileId,
        CreatedAt = createdAt ?? DateTime.UtcNow,
    };

    private static CloudFileEntry Entry(long ownerId, Guid fileId, bool isDeleted = false, string? name = null) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = ownerId,
        DirectoryId = CloudHierarchyStorage.RootDirectoryId,
        FileId = fileId,
        Name = name ?? Guid.NewGuid().ToString(),
        IsDeleted = isDeleted,
        DeletedAt = isDeleted ? DateTime.UtcNow : null,
        CreatedAt = DateTime.UtcNow,
    };

    public void Dispose() => _database.Dispose();
}
