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

    private Guid AddFile(string storageProfileId)
    {
        var id = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(new UploadFile
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = storageProfileId,
            Filename = $"{id}.jpg",
            Size = 1
        });
        return id;
    }

    public void Dispose() => _db.Dispose();
}
