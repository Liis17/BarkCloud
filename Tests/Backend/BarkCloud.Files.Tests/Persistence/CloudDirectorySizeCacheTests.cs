using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class CloudDirectorySizeCacheTests
{
    private const long OwnerId = 42;

    [Fact]
    public async Task GetSubtreeSizes_SumsLiveFilesOfWholeSubtree()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var child = Directory("child", root.Id);
        var grandchild = Directory("grandchild", child.Id);
        var (rootFile, rootFileBlob) = Entry(root.Id, "root.txt", 10);
        var (childFile, childFileBlob) = Entry(child.Id, "child.txt", 20);
        var (grandchildFile, grandchildFileBlob) = Entry(grandchild.Id, "grandchild.txt", 40);
        var (trashed, trashedBlob) = Entry(child.Id, "trashed.txt", 80, isDeleted: true);
        await AddAsync(database, new[] { root, child, grandchild },
            new[] { rootFile, childFile, grandchildFile, trashed },
            new[] { rootFileBlob, childFileBlob, grandchildFileBlob, trashedBlob });

        var storage = new CloudHierarchyStorage(database.Context);
        var sizes = await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, root.Id), await ReadDirectory(database, child.Id) });

        sizes[root.Id].Should().Be(70);
        sizes[child.Id].Should().Be(60);
    }

    [Fact]
    public async Task GetSubtreeSizes_ServesCachedValueUntilInvalidated()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var (file, blob) = Entry(root.Id, "a.txt", 10);
        await AddAsync(database, new[] { root }, new[] { file }, new[] { blob });
        var storage = new CloudHierarchyStorage(database.Context);

        await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, root.Id) });
        // Прямой UPDATE минует SaveChanges, поэтому инвалидации нет: значение должно остаться из кеша.
        await database.Context.UploadedFiles.Where(x => x.Id == blob.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Size, 999L));
        var cached = await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, root.Id) });
        cached[root.Id].Should().Be(10);

        var (extra, extraBlob) = Entry(root.Id, "b.txt", 5);
        database.Context.UploadedFiles.Add(extraBlob);
        database.Context.CloudFileEntries.Add(extra);
        await database.Context.SaveChangesAsync();
        var recomputed = await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, root.Id) });
        recomputed[root.Id].Should().Be(999 + 5);
    }

    [Fact]
    public async Task AddingFile_InvalidatesDirectoryAndAncestorsOnly()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var child = Directory("child", root.Id);
        var sibling = Directory("sibling", root.Id);
        var (sibFile, sibBlob) = Entry(sibling.Id, "sib.txt", 1);
        await AddAsync(database, new[] { root, child, sibling }, new[] { sibFile }, new[] { sibBlob });
        var storage = new CloudHierarchyStorage(database.Context);
        await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, root.Id), await ReadDirectory(database, child.Id), await ReadDirectory(database, sibling.Id) });

        var (added, addedBlob) = Entry(child.Id, "new.txt", 7);
        database.Context.UploadedFiles.Add(addedBlob);
        database.Context.CloudFileEntries.Add(added);
        await database.Context.SaveChangesAsync();

        (await ReadDirectory(database, root.Id)).SubtreeSizeBytes.Should().BeNull();
        (await ReadDirectory(database, child.Id)).SubtreeSizeBytes.Should().BeNull();
        (await ReadDirectory(database, sibling.Id)).SubtreeSizeBytes.Should().Be(1);
    }

    [Fact]
    public async Task RenamingEntry_DoesNotInvalidate()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var (file, blob) = Entry(root.Id, "a.txt", 10);
        await AddAsync(database, new[] { root }, new[] { file }, new[] { blob });
        var storage = new CloudHierarchyStorage(database.Context);
        await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, root.Id) });
        var before = await ReadDirectory(database, root.Id);

        var tracked = await database.Context.CloudFileEntries.SingleAsync(x => x.Id == file.Id);
        tracked.Name = "renamed.txt";
        await database.Context.SaveChangesAsync();

        var after = await ReadDirectory(database, root.Id);
        after.SubtreeSizeBytes.Should().Be(before.SubtreeSizeBytes);
        after.SizeVersion.Should().Be(before.SizeVersion);
    }

    [Fact]
    public async Task MovingEntry_InvalidatesSourceAndTargetFolders()
    {
        using var database = new SqliteFilesContext();
        var source = Directory("source");
        var target = Directory("target");
        var (file, blob) = Entry(source.Id, "a.txt", 10);
        await AddAsync(database, new[] { source, target }, new[] { file }, new[] { blob });
        var storage = new CloudHierarchyStorage(database.Context);
        await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, source.Id), await ReadDirectory(database, target.Id) });

        var tracked = await database.Context.CloudFileEntries.SingleAsync(x => x.Id == file.Id);
        tracked.DirectoryId = target.Id;
        await database.Context.SaveChangesAsync();

        (await ReadDirectory(database, source.Id)).SubtreeSizeBytes.Should().BeNull();
        (await ReadDirectory(database, target.Id)).SubtreeSizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task DeletingEmptyDirectory_InvalidatesParentAndAncestors()
    {
        using var database = new SqliteFilesContext();
        var grandparent = Directory("grandparent");
        var parent = Directory("parent", grandparent.Id);
        var empty = Directory("empty", parent.Id);
        await AddAsync(database, new[] { grandparent, parent, empty }, Array.Empty<CloudFileEntry>(), Array.Empty<UploadFile>());
        var storage = new CloudHierarchyStorage(database.Context);
        await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, grandparent.Id), await ReadDirectory(database, parent.Id) });
        (await ReadDirectory(database, parent.Id)).SubtreeSizeBytes.Should().Be(0);

        var tracked = await database.Context.CloudDirectories.SingleAsync(x => x.Id == empty.Id);
        database.Context.CloudDirectories.Remove(tracked);
        await database.Context.SaveChangesAsync();

        (await ReadDirectory(database, parent.Id)).SubtreeSizeBytes.Should().BeNull();
        (await ReadDirectory(database, grandparent.Id)).SubtreeSizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task MovingDirectory_InvalidatesOldAndNewParentsIncludingAncestors()
    {
        using var database = new SqliteFilesContext();
        var grandparent = Directory("grandparent");
        var oldParent = Directory("old", grandparent.Id);
        var newParent = Directory("new");
        var moved = Directory("moved", oldParent.Id);
        var (file, blob) = Entry(moved.Id, "a.txt", 10);
        await AddAsync(database, new[] { grandparent, oldParent, newParent, moved }, new[] { file }, new[] { blob });
        var storage = new CloudHierarchyStorage(database.Context);
        await storage.GetSubtreeSizes(OwnerId, new[]
        {
            await ReadDirectory(database, grandparent.Id), await ReadDirectory(database, oldParent.Id), await ReadDirectory(database, newParent.Id)
        });

        var tracked = await database.Context.CloudDirectories.SingleAsync(x => x.Id == moved.Id);
        tracked.ParentId = newParent.Id;
        await database.Context.SaveChangesAsync();

        (await ReadDirectory(database, grandparent.Id)).SubtreeSizeBytes.Should().BeNull();
        (await ReadDirectory(database, oldParent.Id)).SubtreeSizeBytes.Should().BeNull();
        (await ReadDirectory(database, newParent.Id)).SubtreeSizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task ResavingUnchangedFile_DoesNotInvalidate()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var (file, blob) = Entry(root.Id, "a.txt", 10);
        await AddAsync(database, new[] { root }, new[] { file }, new[] { blob });
        var storage = new CloudHierarchyStorage(database.Context);
        await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, root.Id) });
        var before = await ReadDirectory(database, root.Id);

        // Как LegacyPreviewBackfillService: объект без отслеживания, сохраняется через Update().
        database.Context.ChangeTracker.Clear();
        var detached = await database.Context.UploadedFiles.AsNoTracking().SingleAsync(x => x.Id == blob.Id);
        database.Context.UploadedFiles.Update(detached);
        await database.Context.SaveChangesAsync();

        var after = await ReadDirectory(database, root.Id);
        after.SubtreeSizeBytes.Should().Be(before.SubtreeSizeBytes);
        after.SizeVersion.Should().Be(before.SizeVersion);
    }

    [Fact]
    public async Task ResizingFile_InvalidatesFoldersWithItsEntries()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var (file, blob) = Entry(root.Id, "photo.heic", 10);
        await AddAsync(database, new[] { root }, new[] { file }, new[] { blob });
        var storage = new CloudHierarchyStorage(database.Context);
        await storage.GetSubtreeSizes(OwnerId, new[] { await ReadDirectory(database, root.Id) });

        var trackedBlob = await database.Context.UploadedFiles.SingleAsync(x => x.Id == blob.Id);
        trackedBlob.Size = 4;
        await database.Context.SaveChangesAsync();

        (await ReadDirectory(database, root.Id)).SubtreeSizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task StaleRecompute_DoesNotStoreValueAfterNewerInvalidation()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var (file, blob) = Entry(root.Id, "a.txt", 10);
        await AddAsync(database, new[] { root }, new[] { file }, new[] { blob });
        var snapshot = await ReadDirectory(database, root.Id);

        var (added, addedBlob) = Entry(root.Id, "b.txt", 5);
        database.Context.UploadedFiles.Add(addedBlob);
        database.Context.CloudFileEntries.Add(added);
        await database.Context.SaveChangesAsync();

        var storage = new CloudHierarchyStorage(database.Context);
        var sizes = await storage.GetSubtreeSizes(OwnerId, new[] { snapshot });

        sizes[root.Id].Should().Be(15);
        var stored = await ReadDirectory(database, root.Id);
        stored.SubtreeSizeBytes.Should().BeNull();
        stored.SizeVersion.Should().Be(snapshot.SizeVersion + 1);
    }

    private static async Task<CloudDirectory> ReadDirectory(SqliteFilesContext database, Guid id) =>
        await database.Context.CloudDirectories.AsNoTracking().SingleAsync(x => x.Id == id);

    private static async Task AddAsync(
        SqliteFilesContext database, IEnumerable<CloudDirectory> directories,
        IEnumerable<CloudFileEntry> entries, IEnumerable<UploadFile> files)
    {
        database.Context.CloudDirectories.AddRange(directories);
        database.Context.UploadedFiles.AddRange(files);
        database.Context.CloudFileEntries.AddRange(entries);
        await database.Context.SaveChangesAsync();
    }

    private static CloudDirectory Directory(string name, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = OwnerId, ParentId = parentId, Name = name,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static (CloudFileEntry Entry, UploadFile File) Entry(Guid directoryId, string name, long size, bool isDeleted = false)
    {
        var file = new UploadFile
        {
            Id = Guid.NewGuid(), Uploaders = [OwnerId], Type = UploadFileType.CloudFile,
            StorageProfileId = "test-profile", Size = size, CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow, Etag = "etag"
        };
        var entry = new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = directoryId, FileId = file.Id,
            Name = name, IsDeleted = isDeleted, CreatedAt = DateTime.UtcNow
        };
        return (entry, file);
    }
}
