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

    private GrantStorage Storage() => new(_database.Context);

    private async Task Seed(params object[] entities)
    {
        _database.Context.AddRange(entities);
        await _database.Context.SaveChangesAsync();
    }

    private static FileGrant Grant(long ownerId, Guid fileId, long recipientId = RecipientId) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = ownerId,
        RecipientId = recipientId,
        FileId = fileId,
        CreatedAt = DateTime.UtcNow,
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
