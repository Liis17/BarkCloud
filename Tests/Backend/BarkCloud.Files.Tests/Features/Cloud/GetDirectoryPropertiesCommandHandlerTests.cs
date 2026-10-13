using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.GetDirectoryProperties;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.GetDirectoryProperties;

public sealed class GetDirectoryPropertiesCommandHandlerTests
{
    private const long OwnerId = 42;

    [Fact]
    public async Task Handle_CountsRecursiveLiveEntriesAndSize()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var child = Directory("child", root.Id);
        var liveRoot = Entry(root.Id, "root.txt", size: 10);
        var liveChild = Entry(child.Id, "child.txt", size: 20);
        var deleted = Entry(child.Id, "deleted.txt", size: 30, isDeleted: true);
        var foreign = Entry(child.Id, "foreign.txt", size: 40, ownerId: 999);
        database.Context.CloudDirectories.AddRange(root, child);
        database.Context.UploadedFiles.AddRange(liveRoot.File, liveChild.File, deleted.File, foreign.File);
        database.Context.CloudFileEntries.AddRange(liveRoot.Entry, liveChild.Entry, deleted.Entry, foreign.Entry);
        await database.Context.SaveChangesAsync();

        var handler = new GetDirectoryPropertiesCommandHandler(
            new CloudHierarchyStorage(database.Context), UserContextFactory.Create(OwnerId));
        var response = await handler.Handle(new GetDirectoryPropertiesCommand { DirectoryId = root.Id }, default);

        response.Directory.Id.Should().Be(root.Id.ToString());
        response.Directory.Name.Should().Be("root");
        response.FileCount.Should().Be(2);
        response.FolderCount.Should().Be(1);
        response.TotalSizeBytes.Should().Be(30);
        response.HasParent.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MissingOrForeignDirectoryThrows()
    {
        using var database = new SqliteFilesContext();
        var foreign = Directory("foreign", ownerId: 999);
        database.Context.CloudDirectories.Add(foreign);
        await database.Context.SaveChangesAsync();
        var handler = new GetDirectoryPropertiesCommandHandler(
            new CloudHierarchyStorage(database.Context), UserContextFactory.Create(OwnerId));

        Func<Task> missing = () => handler.Handle(new GetDirectoryPropertiesCommand { DirectoryId = Guid.NewGuid() }, default);
        Func<Task> forbidden = () => handler.Handle(new GetDirectoryPropertiesCommand { DirectoryId = foreign.Id }, default);
        await missing.Should().ThrowAsync<DirectoryNotFoundException>();
        await forbidden.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    private static CloudDirectory Directory(string name, Guid? parentId = null, long ownerId = OwnerId) => new()
    {
        Id = Guid.NewGuid(), OwnerId = ownerId, ParentId = parentId, Name = name,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static (CloudFileEntry Entry, global::BarkCloud.Files.Domain.UploadFile File) Entry(
        Guid directoryId, string name, long size, long ownerId = OwnerId, bool isDeleted = false)
    {
        var fileId = Guid.NewGuid();
        var file = new global::BarkCloud.Files.Domain.UploadFile
        {
            Id = fileId, Uploaders = [ownerId], Type = UploadFileType.CloudFile,
            StorageProfileId = "test-profile", Size = size, CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow, Etag = "etag"
        };
        var entry = new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, DirectoryId = directoryId, FileId = fileId,
            Name = name, IsDeleted = isDeleted, CreatedAt = DateTime.UtcNow
        };
        return (entry, file);
    }
}
