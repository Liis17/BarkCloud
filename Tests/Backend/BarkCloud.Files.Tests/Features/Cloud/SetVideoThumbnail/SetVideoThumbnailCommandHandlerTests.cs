using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.SetVideoThumbnail;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.SetVideoThumbnail;

public class SetVideoThumbnailCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IUploadedFilesStorage> _files = new();

    // IO-сервисы (ImageCompressor/PreviewPersistenceService/S3Uploader/S3BucketRegistry) не достигаются:
    // все проверяемые ветки бросают исключение до первого обращения к S3/сжатию.
    private SetVideoThumbnailCommandHandler CreateSut() => new(
        _files.Object, null!, null!, null!, null!,
        UserContextFactory.Create(OwnerId),
        NullLogger<SetVideoThumbnailCommandHandler>.Instance);

    [Fact]
    public async Task Handle_VideoNotFound_Throws()
    {
        var sourceId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);
        _files.Setup(s => s.GetFile(sourceId)).ReturnsAsync(new UploadFileEntity { Id = sourceId, MediaKind = MediaKind.Photo, Uploaders = new() { OwnerId } });

        var act = () => CreateSut().Handle(new SetVideoThumbnailCommand { VideoFileId = Guid.NewGuid(), SourceImageFileId = sourceId }, default);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_SourceNotFound_Throws()
    {
        var videoId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);
        _files.Setup(s => s.GetFile(videoId)).ReturnsAsync(new UploadFileEntity { Id = videoId, MediaKind = MediaKind.Video, Uploaders = new() { OwnerId } });

        var act = () => CreateSut().Handle(new SetVideoThumbnailCommand { VideoFileId = videoId, SourceImageFileId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_ForeignFile_ThrowsAccessDenied()
    {
        var videoId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(videoId)).ReturnsAsync(new UploadFileEntity { Id = videoId, MediaKind = MediaKind.Video, Uploaders = new() { 999 } });
        _files.Setup(s => s.GetFile(sourceId)).ReturnsAsync(new UploadFileEntity { Id = sourceId, MediaKind = MediaKind.Photo, Uploaders = new() { OwnerId } });

        var act = () => CreateSut().Handle(new SetVideoThumbnailCommand { VideoFileId = videoId, SourceImageFileId = sourceId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_WrongMediaKind_Throws()
    {
        var videoId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        // "Видео" на деле фото — источник смены превью некорректен.
        _files.Setup(s => s.GetFile(videoId)).ReturnsAsync(ReadyFile(videoId, MediaKind.Photo));
        _files.Setup(s => s.GetFile(sourceId)).ReturnsAsync(ReadyFile(sourceId, MediaKind.Photo));

        var act = () => CreateSut().Handle(new SetVideoThumbnailCommand { VideoFileId = videoId, SourceImageFileId = sourceId }, default);

        await act.Should().ThrowAsync<InvalidThumbnailSourceException>();
    }

    [Fact]
    public async Task Handle_UsesLandscapeVideoPreviewGenerator()
    {
        var videoId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var video = new UploadFileEntity
        {
            Id = videoId,
            Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Video,
            Uploaders = new() { OwnerId },
            UploadedAt = DateTime.UtcNow,
            Etag = "video-etag"
        };
        var source = new UploadFileEntity
        {
            Id = sourceId,
            Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Photo,
            Uploaders = new() { OwnerId },
            UploadedAt = DateTime.UtcNow,
            Etag = "source-etag"
        };

        var files = new Mock<IUploadedFilesStorage>();
        files.Setup(s => s.GetFile(videoId)).ReturnsAsync(video);
        files.Setup(s => s.GetFile(sourceId)).ReturnsAsync(source);
        files.Setup(s => s.RemovePreviewsForOriginal(videoId, OwnerId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var bucketRegistry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
        bucketRegistry.Setup(r => r.ResolveWriteProfileId(
                UploadFileType.CloudFile, MediaKind.Video, true))
            .Returns("preview-profile");
        bucketRegistry.Setup(r => r.ResolveReadProfileId(It.Is<UploadFileEntity>(f => f.Id == sourceId)))
            .Returns("source-profile");

        var s3 = new Mock<S3Uploader>(bucketRegistry.Object) { CallBase = false };
        s3.Setup(u => u.DownloadAsync("source-profile", sourceId.ToString()))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        var compressor = new Mock<ImageCompressor>();
        var previews = new List<MultiPreviewItem>
        {
            new(1024, 1024, 576, new byte[] { 4, 5, 6 })
        };
        compressor.Setup(c => c.GenerateVideoPreviewsAsync(
                It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(previews);

        var previewPersistence = new Mock<PreviewPersistenceService>(
            null!, null!, s3.Object, null!, NullLogger<PreviewPersistenceService>.Instance)
        {
            CallBase = false
        };
        previewPersistence.Setup(p => p.PersistPreviewsAsync(
                video, previews, "preview-profile", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new SetVideoThumbnailCommandHandler(
            files.Object,
            compressor.Object,
            previewPersistence.Object,
            s3.Object,
            bucketRegistry.Object,
            UserContextFactory.Create(OwnerId),
            NullLogger<SetVideoThumbnailCommandHandler>.Instance);

        var response = await handler.Handle(
            new SetVideoThumbnailCommand { VideoFileId = videoId, SourceImageFileId = sourceId }, default);

        response.Should().NotBeNull();
        compressor.Verify(c => c.GenerateVideoPreviewsAsync(
            It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()), Times.Once);
        compressor.Verify(c => c.GenerateMultiplePreviewsAsync(
            It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()), Times.Never);
        files.Verify(s => s.RemovePreviewsForOriginal(videoId, OwnerId, It.IsAny<CancellationToken>()), Times.Once);
        previewPersistence.Verify(p => p.PersistPreviewsAsync(
            video, previews, "preview-profile", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_VideoIsNotReady_ThrowsFileNotReady()
    {
        var videoId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(videoId)).ReturnsAsync(new UploadFileEntity
        {
            Id = videoId,
            MediaKind = MediaKind.Video,
            Uploaders = new() { OwnerId }
        });
        _files.Setup(s => s.GetFile(sourceId)).ReturnsAsync(ReadyFile(sourceId, MediaKind.Photo));

        var act = () => CreateSut().Handle(
            new SetVideoThumbnailCommand { VideoFileId = videoId, SourceImageFileId = sourceId }, default);

        await act.Should().ThrowAsync<FileNotReadyException>();
    }

    private static UploadFileEntity ReadyFile(Guid id, MediaKind mediaKind) => new()
    {
        Id = id,
        MediaKind = mediaKind,
        Uploaders = new() { OwnerId },
        UploadedAt = DateTime.UtcNow,
        Etag = "etag"
    };
}
