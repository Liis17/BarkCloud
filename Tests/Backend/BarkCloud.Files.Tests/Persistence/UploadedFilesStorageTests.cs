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

    [Fact]
    public async Task GetUserMediaStats_MatchesGalleryFiltersForKindReadyStatePreviewsAndTrash()
    {
        const long ownerId = 42;
        var now = DateTime.UtcNow;
        var visibleVideoOne = ReadyFile(ownerId, MediaKind.Video, 100);
        var visibleVideoTwo = ReadyFile(ownerId, MediaKind.Video, 200);
        var visiblePhoto = ReadyFile(ownerId, MediaKind.Photo, 300);
        var previewVideo = ReadyFile(ownerId, MediaKind.Video, 400);
        var trashedVideo = ReadyFile(ownerId, MediaKind.Video, 500);
        var restoredVideo = ReadyFile(ownerId, MediaKind.Video, 600);
        var pendingVideo = ReadyFile(ownerId, MediaKind.Video, 700);
        pendingVideo.Etag = null;
        pendingVideo.UploadedAt = null;
        var anotherUsersVideo = ReadyFile(7, MediaKind.Video, 800);

        _database.Context.UploadedFiles.AddRange(
            visibleVideoOne, visibleVideoTwo, visiblePhoto, previewVideo,
            trashedVideo, restoredVideo, pendingVideo, anotherUsersVideo);
        _database.Context.FilePreviews.Add(new FilePreview
        {
            Id = Guid.NewGuid(),
            OriginalFileId = visibleVideoOne.Id,
            PreviewFileId = previewVideo.Id,
            TargetWidth = 128,
            CreatedAt = now
        });
        _database.Context.CloudFileEntries.AddRange(
            new CloudFileEntry
            {
                Id = Guid.NewGuid(), OwnerId = ownerId, DirectoryId = Guid.NewGuid(),
                FileId = trashedVideo.Id, Name = "deleted.mp4", IsDeleted = true, CreatedAt = now
            },
            new CloudFileEntry
            {
                Id = Guid.NewGuid(), OwnerId = ownerId, DirectoryId = Guid.NewGuid(),
                FileId = restoredVideo.Id, Name = "old.mp4", IsDeleted = true, CreatedAt = now
            },
            new CloudFileEntry
            {
                Id = Guid.NewGuid(), OwnerId = ownerId, DirectoryId = Guid.NewGuid(),
                FileId = restoredVideo.Id, Name = "restored.mp4", IsDeleted = false, CreatedAt = now
            });
        await _database.Context.SaveChangesAsync();

        var storage = new UploadedFilesStorage(_database.Context);
        var videoStats = await storage.GetUserMediaStats(ownerId, MediaKind.Video);
        var photoStats = await storage.GetUserMediaStats(ownerId, MediaKind.Photo);

        videoStats.Should().Be(new UserMediaStats(3, 900));
        photoStats.Should().Be(new UserMediaStats(1, 300));
    }

    [Fact]
    public async Task GetUserMediaStats_EmptyGalleryReturnsZeroValues()
    {
        var stats = await new UploadedFilesStorage(_database.Context)
            .GetUserMediaStats(42, MediaKind.Video);

        stats.Should().Be(new UserMediaStats(0, 0));
    }

    private static UploadFile ReadyFile(long ownerId, MediaKind kind, long size) => new()
    {
        Id = Guid.NewGuid(),
        Uploaders = [ownerId],
        CreatedAt = DateTime.UtcNow,
        UploadedAt = DateTime.UtcNow,
        Etag = "etag",
        Type = UploadFileType.CloudFile,
        StorageProfileId = "test-profile",
        MediaKind = kind,
        Size = size
    };

    public void Dispose() => _database.Dispose();
}
