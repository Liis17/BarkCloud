using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class DirectoryVisibilityQueryTests : IDisposable
{
    private const long OwnerId = 42;
    private readonly SqliteFilesContext _database = new();

    [Fact]
    public async Task SearchFileEntriesPage_ExcludesHiddenEntriesOnlyWhenRequestedAndBeforePaging()
    {
        var now = DateTime.UtcNow;
        var hiddenDirectory = new CloudDirectory
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, Name = "hidden", EffectiveHideFromGallery = true,
            CreatedAt = now, UpdatedAt = now
        };
        var openDirectory = new CloudDirectory
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, Name = "open", InheritVisibility = false,
            CreatedAt = now, UpdatedAt = now
        };
        var hidden = Entry(hiddenDirectory.Id, "report-hidden.jpg", now.AddMinutes(-1));
        var visible = Entry(openDirectory.Id, "report-visible.jpg", now.AddMinutes(-2));
        var root = Entry(Guid.Empty, "report-root.jpg", now.AddMinutes(-3));
        var shared = Entry(hiddenDirectory.Id, "report-shared.jpg", now.AddMinutes(-4));
        var sharedForOther = new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = 7, DirectoryId = Guid.Empty, FileId = shared.FileId,
            Name = "report-shared.jpg", CreatedAt = now.AddMinutes(-4)
        };

        _database.Context.CloudDirectories.AddRange(hiddenDirectory, openDirectory);
        CloudFileEntryFixtures.AddOriginals(_database.Context, [hidden, visible, root, shared, sharedForOther]);
        _database.Context.CloudFileEntries.AddRange(hidden, visible, root, shared, sharedForOther);
        await _database.Context.SaveChangesAsync();

        var storage = new CloudHierarchyStorage(_database.Context);
        var filtered = await storage.SearchFileEntriesPage(
            OwnerId, "report", null, null, 1, excludeHiddenFromGallery: true);
        var unfiltered = await storage.SearchFileEntriesPage(OwnerId, "report", null, null, 10);
        var otherOwner = await storage.SearchFileEntriesPage(7, "report", null, null, 10,
            excludeHiddenFromGallery: true);

        filtered.Select(x => x.Id).Should().Equal([visible.Id, root.Id], "hidden rows are removed before limit+1");
        unfiltered.Select(x => x.Id).Should().Contain(hidden.Id);
        otherOwner.Select(x => x.Id).Should().ContainSingle().Which.Should().Be(sharedForOther.Id);
    }

    private static CloudFileEntry Entry(Guid directoryId, string name, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = directoryId, FileId = Guid.NewGuid(),
        Name = name, CreatedAt = createdAt
    };

    public void Dispose() => _database.Dispose();
}
