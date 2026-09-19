using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class CloudHierarchyStorageTests : IDisposable
{
    private const long OwnerId = 42;
    private readonly SqliteFilesContext _database = new();

    [Fact]
    public async Task ListFilesInDirectoryPage_UsesStableCursorAndSkipsDeletedEntries()
    {
        var first = Entry("a.txt", Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var second = Entry("b.txt", Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var third = Entry("c.txt", Guid.Parse("00000000-0000-0000-0000-000000000003"));
        var deleted = Entry("deleted.txt", Guid.NewGuid(), isDeleted: true);
        var anotherOwner = Entry("other-owner.txt", Guid.NewGuid(), ownerId: 99);

        _database.Context.CloudFileEntries.AddRange(first, second, third, deleted, anotherOwner);
        await _database.Context.SaveChangesAsync();

        var storage = new CloudHierarchyStorage(_database.Context);
        var firstPage = await storage.ListFilesInDirectoryPage(
            OwnerId, CloudHierarchyStorage.RootDirectoryId, null, null, limit: 2);
        var visibleFirstPage = firstPage.Take(2).ToList();

        firstPage.Select(x => x.Name).Should().Equal("a.txt", "b.txt", "c.txt");
        visibleFirstPage.Select(x => x.Name).Should().Equal("a.txt", "b.txt");

        var secondPage = await storage.ListFilesInDirectoryPage(
            OwnerId,
            CloudHierarchyStorage.RootDirectoryId,
            visibleFirstPage[^1].Name,
            visibleFirstPage[^1].Id,
            limit: 2);

        secondPage.Select(x => x.Name).Should().Equal("c.txt");
        secondPage.Select(x => x.Id).Should().NotContain(first.Id);
        secondPage.Select(x => x.Id).Should().NotContain(second.Id);
        secondPage.Select(x => x.Name).Should().NotContain("deleted.txt");
        secondPage.Select(x => x.Name).Should().NotContain("other-owner.txt");
    }

    private static CloudFileEntry Entry(string name, Guid id, long ownerId = OwnerId, bool isDeleted = false) => new()
    {
        Id = id,
        OwnerId = ownerId,
        DirectoryId = CloudHierarchyStorage.RootDirectoryId,
        FileId = Guid.NewGuid(),
        Name = name,
        IsDeleted = isDeleted,
        CreatedAt = DateTime.UtcNow,
    };

    public void Dispose() => _database.Dispose();
}
