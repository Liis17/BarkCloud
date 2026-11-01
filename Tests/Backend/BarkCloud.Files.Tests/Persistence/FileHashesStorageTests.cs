using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class FileHashesStorageTests : IDisposable
{
    private readonly SqliteFilesContext _db = new();

    [Fact]
    public async Task GetFileIdByHash_WithStorageProfile_ReturnsOnlyFileFromRequestedProfile()
    {
        var legacyFileId = AddFile("cloud-files-old-v1");
        var previewsFileId = AddFile("previews-v1");
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        _db.Context.FileHashes.AddRange(
            new FileHash { FileId = legacyFileId, Hash = hash },
            new FileHash { FileId = previewsFileId, Hash = hash });
        await _db.Context.SaveChangesAsync();

        var storage = new FileHashesStorage(_db.Context);

        (await storage.GetFileIdByHash(hash, "previews-v1")).Should().Be(previewsFileId);
        (await storage.GetFileIdByHash(hash, "images-v1")).Should().BeNull();
        (await storage.GetFileIdByHash(hash)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetLiveHashMatchesForOwner_ReturnsOnlyOwnerLiveEntriesWithReadyFlag()
    {
        const string hash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var readyFileId = AddFile("images-v1", ready: true);
        var pendingFileId = AddFile("images-v1");
        var trashedFileId = AddFile("images-v1", ready: true);
        var otherOwnerFileId = AddFile("images-v1", ready: true);

        _db.Context.FileHashes.AddRange(
            new FileHash { FileId = readyFileId, Hash = hash },
            new FileHash { FileId = pendingFileId, Hash = hash },
            new FileHash { FileId = trashedFileId, Hash = hash },
            new FileHash { FileId = otherOwnerFileId, Hash = hash });
        _db.Context.CloudFileEntries.AddRange(
            NewEntry(ownerId: 7, readyFileId),
            NewEntry(ownerId: 7, pendingFileId),
            NewEntry(ownerId: 7, trashedFileId, isDeleted: true),
            NewEntry(ownerId: 8, otherOwnerFileId));
        await _db.Context.SaveChangesAsync();

        var storage = new FileHashesStorage(_db.Context);

        var matches = await storage.GetLiveHashMatchesForOwner(7, [hash]);

        matches.Should().BeEquivalentTo(
        [
            new OwnerHashMatch(hash, readyFileId, IsReady: true),
            new OwnerHashMatch(hash, pendingFileId, IsReady: false),
        ]);
    }

    private Guid AddFile(string storageProfileId, bool ready = false)
    {
        var id = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(new UploadFile
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = ready ? DateTime.UtcNow : null,
            Etag = ready ? "etag" : null,
            Type = UploadFileType.CloudFile,
            StorageProfileId = storageProfileId,
            Filename = $"{id}.jpg",
            Size = 1
        });
        return id;
    }

    private static CloudFileEntry NewEntry(long ownerId, Guid fileId, bool isDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = ownerId,
        DirectoryId = Guid.NewGuid(),
        FileId = fileId,
        Name = "f.jpg",
        CreatedAt = DateTime.UtcNow,
        IsDeleted = isDeleted,
        DeletedAt = isDeleted ? DateTime.UtcNow : null,
    };

    public void Dispose() => _db.Dispose();
}
