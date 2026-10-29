using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

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
    public async Task VideoVariantCountsTowardQuotaButIsNotListedAsUserMediaOrTypeBreakdown()
    {
        const long ownerId = 42;
        var original = ReadyFile(ownerId, MediaKind.Video, 80);
        var variant = ReadyFile(ownerId, MediaKind.Video, 20);
        variant.Type = UploadFileType.VideoVariant;
        _database.Context.UploadedFiles.AddRange(original, variant);
        await _database.Context.SaveChangesAsync();

        var storage = new UploadedFilesStorage(_database.Context);
        var media = await storage.ListUserMediaPage(ownerId, MediaKind.Video, null, null, 50);
        var usedBytes = await storage.GetUserStorageUsed(ownerId);
        var byType = await storage.GetUserStorageByType(ownerId);

        media.Should().ContainSingle().Which.Id.Should().Be(original.Id);
        usedBytes.Should().Be(100);
        byType.Should().ContainKey(UploadFileType.CloudFile).WhoseValue.Should().Be(80);
        byType.Should().NotContainKey(UploadFileType.VideoVariant);
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

    [Fact]
    public async Task GalleryQueries_ExcludeHiddenFilesBeforePagingAndKeepVisibilityOwnerScoped()
    {
        const long ownerId = 42;
        const long otherOwnerId = 7;
        var today = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var hiddenDirectory = new CloudDirectory
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Name = "hidden", EffectiveHideFromGallery = true,
            CreatedAt = today, UpdatedAt = today
        };
        var openDirectory = new CloudDirectory
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Name = "open", InheritVisibility = false,
            CreatedAt = today, UpdatedAt = today
        };
        var hiddenPhoto = ReadyFile(ownerId, MediaKind.Photo, 999);
        hiddenPhoto.CreatedAt = today;
        hiddenPhoto.Filename = "hidden.jpg";
        var visibleNew = ReadyFile(ownerId, MediaKind.Photo, 300);
        visibleNew.Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        visibleNew.CreatedAt = today.AddMinutes(-1);
        visibleNew.Filename = "visible-new.jpg";
        var visibleRoot = ReadyFile(ownerId, MediaKind.Photo, 200);
        visibleRoot.CreatedAt = today.AddMinutes(-2);
        visibleRoot.Filename = "visible-root.jpg";
        var shared = ReadyFile(ownerId, MediaKind.Photo, 400);
        shared.Uploaders.Add(otherOwnerId);
        shared.CreatedAt = today.AddSeconds(-1);
        shared.Filename = "shared.jpg";
        var hiddenVideo = ReadyFile(ownerId, MediaKind.Video, 500);
        hiddenVideo.CreatedAt = today.AddSeconds(-2);
        var visibleVideo = ReadyFile(ownerId, MediaKind.Video, 100);
        visibleVideo.CreatedAt = today.AddSeconds(-3);

        CloudFileEntry Entry(UploadFile file, Guid directoryId, long entryOwnerId = ownerId) => new()
        {
            Id = Guid.NewGuid(), OwnerId = entryOwnerId, DirectoryId = directoryId,
            FileId = file.Id, Name = file.Filename ?? "video.mp4", CreatedAt = file.CreatedAt
        };

        var entries = new[]
        {
            Entry(hiddenPhoto, hiddenDirectory.Id), Entry(visibleNew, openDirectory.Id),
            Entry(visibleRoot, Guid.Empty), Entry(shared, hiddenDirectory.Id),
            Entry(hiddenVideo, hiddenDirectory.Id), Entry(visibleVideo, openDirectory.Id)
        };
        var files = new[] { hiddenPhoto, visibleNew, visibleRoot, shared, hiddenVideo, visibleVideo };
        _database.Context.CloudDirectories.AddRange(hiddenDirectory, openDirectory);
        _database.Context.UploadedFiles.AddRange(files);
        _database.Context.CloudFileEntries.AddRange(entries);
        _database.Context.FileMetadata.AddRange(files.Select(file => new FileMetadata
        {
            FileId = file.Id, CreatedAt = today, TakenAt = today,
            Latitude = 40, Longitude = 20
        }));
        await _database.Context.SaveChangesAsync();

        var storage = new UploadedFilesStorage(_database.Context);
        var first = await storage.ListUserMediaPage(ownerId, MediaKind.Photo, null, null, 1);
        var next = await storage.ListUserMediaPage(ownerId, MediaKind.Photo, first[0].CreatedAt, first[0].Id, 1);
        var legacyImages = await storage.ListUserImagesPage(ownerId, null, null, 10);
        var stats = await storage.GetUserMediaStats(ownerId, MediaKind.Photo);
        var memories = await storage.ListMemoriesForDay(ownerId, today.Month, today.Day, 10);
        var locations = await storage.ListMediaWithLocationPage(ownerId, null, null, 10);
        var otherOwnerPage = await storage.ListUserMediaPage(otherOwnerId, MediaKind.Photo, null, null, 10);

        first.Select(x => x.Id).Should().Equal([visibleNew.Id, visibleRoot.Id], "filtering happens before limit+1");
        next.Should().ContainSingle().Which.Id.Should().Be(visibleRoot.Id);
        legacyImages.Select(x => x.Id).Should().BeEquivalentTo([visibleNew.Id, visibleRoot.Id]);
        stats.Should().Be(new UserMediaStats(2, 500));
        memories.Select(x => x.File.Id).Should().BeEquivalentTo([visibleNew.Id, visibleRoot.Id, visibleVideo.Id]);
        locations.Select(x => x.File.Id).Should().BeEquivalentTo([visibleNew.Id, visibleRoot.Id, visibleVideo.Id]);
        otherOwnerPage.Should().ContainSingle().Which.Id.Should().Be(shared.Id);
    }

    [Fact]
    public async Task AddUploaderToFile_AddsOwnerOnce_AndIgnoresUnknownFile()
    {
        var file = ReadyFile(1, MediaKind.Photo, 10);
        _database.Context.UploadedFiles.Add(file);
        await _database.Context.SaveChangesAsync();
        var storage = new UploadedFilesStorage(_database.Context);

        await storage.AddUploaderToFile(file.Id, 2);
        await storage.AddUploaderToFile(file.Id, 2);
        await storage.AddUploaderToFile(Guid.NewGuid(), 3);

        (await StoredUploaders(file.Id)).Should().Equal(1, 2);
    }

    [Fact]
    public async Task RemoveUploaderFromFile_RemovesOnlyThatOwner_AndIgnoresMissingOwner()
    {
        var file = ReadyFile(1, MediaKind.Photo, 10);
        file.Uploaders.Add(2);
        _database.Context.UploadedFiles.Add(file);
        await _database.Context.SaveChangesAsync();
        var storage = new UploadedFilesStorage(_database.Context);

        await storage.RemoveUploaderFromFile(file.Id, 1);
        await storage.RemoveUploaderFromFile(file.Id, 1);
        await storage.RemoveUploaderFromFile(Guid.NewGuid(), 2);

        (await StoredUploaders(file.Id)).Should().Equal(2);
    }

    [Fact]
    public async Task UpdateFile_StaleSnapshot_DoesNotOverwriteUploaders()
    {
        var file = ReadyFile(1, MediaKind.Photo, 10);
        _database.Context.UploadedFiles.Add(file);
        await _database.Context.SaveChangesAsync();
        _database.Context.ChangeTracker.Clear();
        var storage = new UploadedFilesStorage(_database.Context);
        var stale = (await storage.GetFile(file.Id))!;

        // Пока у вызывающего на руках устаревший снимок, владельцев меняет другой запрос.
        using (var other = _database.CreateAdditionalContext())
            await new UploadedFilesStorage(other).AddUploaderToFile(file.Id, 2);
        stale.Size = 99;
        await storage.UpdateFile(stale);

        _database.Context.ChangeTracker.Clear();
        var stored = await _database.Context.UploadedFiles.SingleAsync(x => x.Id == file.Id);
        stored.Size.Should().Be(99);
        stored.Uploaders.Should().Equal(1, 2);
    }

    [Fact]
    public async Task RemovePreviewsForOriginal_KeepsSharedPreviewOwner_AndReleasesPrivateOne()
    {
        const long ownerId = 1;
        var video = ReadyFile(ownerId, MediaKind.Video, 10);
        var otherVideo = ReadyFile(ownerId, MediaKind.Video, 10);
        var shared = ReadyFile(ownerId, MediaKind.Photo, 1);
        var privatePreview = ReadyFile(ownerId, MediaKind.Photo, 1);
        _database.Context.UploadedFiles.AddRange(video, otherVideo, shared, privatePreview);
        _database.Context.FilePreviews.AddRange(
            Preview(video.Id, shared.Id, 128), Preview(otherVideo.Id, shared.Id, 128),
            Preview(video.Id, privatePreview.Id, 512));
        await _database.Context.SaveChangesAsync();

        await new UploadedFilesStorage(_database.Context).RemovePreviewsForOriginal(video.Id, ownerId);

        (await StoredUploaders(shared.Id)).Should().Equal(ownerId);
        (await StoredUploaders(privatePreview.Id)).Should().BeEmpty();
        (await _database.Context.FilePreviews.AsNoTracking().Select(x => x.OriginalFileId).ToListAsync())
            .Should().OnlyContain(x => x == otherVideo.Id);
    }

    private async Task<List<long>> StoredUploaders(Guid fileId)
    {
        _database.Context.ChangeTracker.Clear();
        return (await _database.Context.UploadedFiles.SingleAsync(x => x.Id == fileId)).Uploaders;
    }

    private static FilePreview Preview(Guid originalId, Guid previewId, int width) => new()
    {
        Id = Guid.NewGuid(),
        OriginalFileId = originalId,
        PreviewFileId = previewId,
        TargetWidth = width,
        ActualWidth = width,
        ActualHeight = width,
        CreatedAt = DateTime.UtcNow
    };

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
