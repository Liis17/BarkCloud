using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class UploadedFilesStorageTests : IDisposable
{
    private readonly SqliteFilesContext _database = new();

    [Fact]
    public async Task GetFiles_ReturnsOnlyReadyFiles()
    {
        var readyId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        _database.Context.UploadedFiles.AddRange(
            new UploadFile
            {
                Id = readyId,
                CreatedAt = DateTime.UtcNow,
                UploadedAt = DateTime.UtcNow,
                Etag = "etag",
                StorageProfileId = "universal-v1"
            },
            new UploadFile
            {
                Id = pendingId,
                CreatedAt = DateTime.UtcNow,
                StorageProfileId = "universal-v1"
            });
        await _database.Context.SaveChangesAsync();

        var files = await new UploadedFilesStorage(_database.Context)
            .GetFiles([readyId, pendingId]);

        files.Should().ContainSingle().Which.Id.Should().Be(readyId);
    }

    [Fact]
    public async Task ListUserMediaPage_FileHasNoEtag_ExcludesIt()
    {
        const long ownerId = 42;
        var now = DateTime.UtcNow;
        var readyId = Guid.NewGuid();
        _database.Context.UploadedFiles.AddRange(
            new UploadFile
            {
                Id = readyId,
                Uploaders = new List<long> { ownerId },
                CreatedAt = now,
                UploadedAt = now,
                Etag = "etag",
                Type = UploadFileType.CloudFile,
                MediaKind = MediaKind.Photo
            },
            new UploadFile
            {
                Id = Guid.NewGuid(),
                Uploaders = new List<long> { ownerId },
                CreatedAt = now.AddSeconds(-1),
                UploadedAt = now,
                Etag = null,
                Type = UploadFileType.CloudFile,
                MediaKind = MediaKind.Photo
            });
        await _database.Context.SaveChangesAsync();

        var files = await new UploadedFilesStorage(_database.Context)
            .ListUserMediaPage(ownerId, MediaKind.Photo, null, null, 50);

        files.Should().ContainSingle().Which.Id.Should().Be(readyId);
    }

    public void Dispose() => _database.Dispose();
}
