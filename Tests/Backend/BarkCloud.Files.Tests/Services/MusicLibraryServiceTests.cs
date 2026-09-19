using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Tests.Services;

public class MusicLibraryServiceTests : IDisposable
{
    private const long OwnerId = 42;
    private readonly SqliteFilesContext _db = new();

    [Fact]
    public async Task ListTracks_FileIsNotReady_HidesIt()
    {
        _db.Context.UploadedFiles.Add(new UploadFile
        {
            Id = Guid.NewGuid(),
            Uploaders = new List<long> { OwnerId },
            Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Audio,
            Filename = "processing.mp3"
        });
        await _db.Context.SaveChangesAsync();
        var files = new Mock<IUploadedFilesStorage>();
        files.Setup(x => x.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        var tempFiles = new Mock<ITempFilesStorage>();
        tempFiles.Setup(x => x.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFile>());
        var service = new MusicLibraryService(
            _db.Context,
            files.Object,
            tempFiles.Object,
            UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        var response = await service.ListTracks(null, 50, null, null, default);

        response.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTrackDownloadUrl_FileIsNotReady_ThrowsFileNotReady()
    {
        var fileId = Guid.NewGuid();
        var files = new Mock<IUploadedFilesStorage>();
        files.Setup(x => x.GetFile(fileId)).ReturnsAsync(new UploadFile
        {
            Id = fileId,
            Uploaders = new List<long> { OwnerId },
            MediaKind = MediaKind.Audio
        });
        var tempFiles = new Mock<ITempFilesStorage>();
        var service = new MusicLibraryService(
            _db.Context,
            files.Object,
            tempFiles.Object,
            UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        var act = () => service.GetTrackDownloadUrl(fileId, default);

        await act.Should().ThrowAsync<FileNotReadyException>();
        tempFiles.Verify(x => x.CreateTempFile(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task AddPlaylistTracks_IgnoresOwnedNonAudioFiles()
    {
        var playlistId = Guid.NewGuid();
        var audioId = Guid.NewGuid();
        var photoId = Guid.NewGuid();
        _db.Context.MusicPlaylists.Add(new MusicPlaylist
        {
            Id = playlistId,
            OwnerId = OwnerId,
            Name = "Playlist",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.Context.SaveChangesAsync();

        var audio = new UploadFile { Id = audioId, Uploaders = [OwnerId], MediaKind = MediaKind.Audio };
        var photo = new UploadFile { Id = photoId, Uploaders = [OwnerId], MediaKind = MediaKind.Photo };
        var files = new Mock<IUploadedFilesStorage>();
        files.Setup(x => x.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync([audio, photo]);
        var service = new MusicLibraryService(
            _db.Context,
            files.Object,
            new Mock<ITempFilesStorage>().Object,
            UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        await service.AddPlaylistTracks(playlistId, [audioId, photoId], default);

        var items = await _db.Context.MusicPlaylistItems.ToListAsync();
        items.Should().ContainSingle().Which.FileId.Should().Be(audioId);
    }

    [Fact]
    public async Task AddPlaylistTracks_RejectsMissingFiles()
    {
        var playlistId = Guid.NewGuid();
        var audioId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        _db.Context.MusicPlaylists.Add(new MusicPlaylist
        {
            Id = playlistId,
            OwnerId = OwnerId,
            Name = "Playlist",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.Context.SaveChangesAsync();

        var files = new Mock<IUploadedFilesStorage>();
        files.Setup(x => x.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync([
            new UploadFile { Id = audioId, Uploaders = [OwnerId], MediaKind = MediaKind.Audio },
        ]);
        var service = new MusicLibraryService(
            _db.Context,
            files.Object,
            new Mock<ITempFilesStorage>().Object,
            UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        var act = () => service.AddPlaylistTracks(playlistId, [audioId, missingId], default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    public void Dispose() => _db.Dispose();
}
