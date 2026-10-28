using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.GetSubdirectorySizes;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Features.Cloud.GetSubdirectorySizes;

public sealed class GetSubdirectorySizesCommandHandlerTests
{
    private const long OwnerId = 42;

    [Fact]
    public async Task Handle_ReturnsSubtreeSizePerChildFolder()
    {
        using var database = new SqliteFilesContext();
        var root = Directory("root");
        var movies = Directory("movies", root.Id);
        var moviesDeep = Directory("deep", movies.Id);
        var photos = Directory("photos", root.Id);
        var (movieFile, movieBlob) = Entry(moviesDeep.Id, "film.mkv", 30);
        var (photo, photoBlob) = Entry(photos.Id, "a.jpg", 5);
        database.Context.CloudDirectories.AddRange(root, movies, moviesDeep, photos);
        database.Context.UploadedFiles.AddRange(movieBlob, photoBlob);
        database.Context.CloudFileEntries.AddRange(movieFile, photo);
        await database.Context.SaveChangesAsync();

        var handler = new GetSubdirectorySizesCommandHandler(
            new CloudHierarchyStorage(database.Context), UserContextFactory.Create(OwnerId));
        var response = await handler.Handle(new GetSubdirectorySizesCommand { ParentId = root.Id }, default);

        response.Sizes.ToDictionary(x => x.DirectoryId, x => x.SizeBytes).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [movies.Id.ToString()] = 30,
            [photos.Id.ToString()] = 5
        });
    }

    [Fact]
    public async Task Handle_MissingOrForeignParentThrows()
    {
        using var database = new SqliteFilesContext();
        var foreign = Directory("foreign", ownerId: 999);
        database.Context.CloudDirectories.Add(foreign);
        await database.Context.SaveChangesAsync();
        var handler = new GetSubdirectorySizesCommandHandler(
            new CloudHierarchyStorage(database.Context), UserContextFactory.Create(OwnerId));

        Func<Task> missing = () => handler.Handle(new GetSubdirectorySizesCommand { ParentId = Guid.NewGuid() }, default);
        Func<Task> forbidden = () => handler.Handle(new GetSubdirectorySizesCommand { ParentId = foreign.Id }, default);
        await missing.Should().ThrowAsync<DirectoryNotFoundException>();
        await forbidden.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    private static CloudDirectory Directory(string name, Guid? parentId = null, long ownerId = OwnerId) => new()
    {
        Id = Guid.NewGuid(), OwnerId = ownerId, ParentId = parentId, Name = name,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static (CloudFileEntry Entry, global::BarkCloud.Files.Domain.UploadFile File) Entry(Guid directoryId, string name, long size)
    {
        var file = new global::BarkCloud.Files.Domain.UploadFile
        {
            Id = Guid.NewGuid(), Uploaders = [OwnerId], Type = UploadFileType.CloudFile,
            StorageProfileId = "test-profile", Size = size, CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow, Etag = "etag"
        };
        var entry = new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = directoryId, FileId = file.Id,
            Name = name, CreatedAt = DateTime.UtcNow
        };
        return (entry, file);
    }
}
