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

        CloudFileEntryFixtures.AddOriginals(_database.Context, [first, second, third, deleted, anotherOwner]);
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

    [Fact]
    public async Task GetSubtree_ReturnsEachDirectoryOnce_ForRegularTree()
    {
        var root = Directory("root");
        var left = Directory("left", root.Id);
        var right = Directory("right", root.Id);
        var leaf = Directory("leaf", left.Id);
        _database.Context.CloudDirectories.AddRange(root, left, right, leaf, Directory("elsewhere"));
        await _database.Context.SaveChangesAsync();

        var subtree = await new CloudHierarchyStorage(_database.Context).GetSubtree(OwnerId, root.Id);

        subtree.Select(x => x.Id).Should().BeEquivalentTo(new[] { root.Id, left.Id, right.Id, leaf.Id });
    }

    [Fact]
    public async Task GetSubtree_TerminatesOnCycle()
    {
        var a = Directory("a");
        var b = Directory("b", a.Id);
        var c = Directory("c", b.Id);
        a.ParentId = c.Id;
        _database.Context.CloudDirectories.AddRange(a, b, c);
        await _database.Context.SaveChangesAsync();

        // Обход на SQLite синхронный, поэтому защита от зависания — отмена по токену.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subtree = await new CloudHierarchyStorage(_database.Context).GetSubtree(OwnerId, a.Id, cts.Token);

        subtree.Select(x => x.Id).Should().BeEquivalentTo(new[] { a.Id, b.Id, c.Id });
    }

    [Fact]
    public async Task GetSubtree_TerminatesOnSelfParent()
    {
        var a = Directory("a");
        a.ParentId = a.Id;
        _database.Context.CloudDirectories.Add(a);
        await _database.Context.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subtree = await new CloudHierarchyStorage(_database.Context).GetSubtree(OwnerId, a.Id, cts.Token);

        subtree.Should().ContainSingle().Which.Id.Should().Be(a.Id);
    }

    private static CloudDirectory Directory(string name, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = OwnerId,
        ParentId = parentId,
        Name = name,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

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
