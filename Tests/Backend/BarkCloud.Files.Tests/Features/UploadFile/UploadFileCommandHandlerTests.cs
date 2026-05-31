using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Features.UploadFile;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.UploadFile;

public class UploadFileCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<IFileHashesStorage> _hashes = new();
    private readonly Mock<IFileMetadataStorage> _metadata = new();
    private readonly Mock<S3BucketRegistry> _bucketRegistry;
    private readonly Mock<S3Uploader> _s3;
    private readonly Mock<ImageCompressor> _imageCompressor;
    private readonly Mock<VideoThumbnailExtractor> _videoExtractor;
    private readonly Mock<HeicImageConverter> _heicConverter;
    private readonly Mock<FileMetadataExtractor> _metadataExtractor;
    private readonly Mock<PreviewPersistenceService> _previewPersistence;

    public UploadFileCommandHandlerTests()
    {
        _bucketRegistry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
        _bucketRegistry.Setup(r => r.GetBucketName(It.IsAny<UploadFileType>())).Returns("test-bucket");

        _s3 = new Mock<S3Uploader>(_bucketRegistry.Object) { CallBase = false };
        _s3.Setup(u => u.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync("etag-123");

        _imageCompressor = new Mock<ImageCompressor>();
        _videoExtractor = new Mock<VideoThumbnailExtractor>(NullLogger<VideoThumbnailExtractor>.Instance);
        _heicConverter = new Mock<HeicImageConverter>(NullLogger<HeicImageConverter>.Instance);
        _metadataExtractor = new Mock<FileMetadataExtractor>(NullLogger<FileMetadataExtractor>.Instance);
        _previewPersistence = new Mock<PreviewPersistenceService>(
            _files.Object, _hashes.Object, _s3.Object, /* FilesContext */ null!,
            NullLogger<PreviewPersistenceService>.Instance);
    }

    private UploadFileCommandHandler CreateSut() => new(
        _files.Object, _hashes.Object, _metadata.Object, _s3.Object, _bucketRegistry.Object,
        _imageCompressor.Object, _videoExtractor.Object, _heicConverter.Object,
        _metadataExtractor.Object, _previewPersistence.Object, NullLogger<UploadFileCommandHandler>.Instance);

    private static Stream MakeStream(string content = "hello") => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Handle_FileNotFound_Throws()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id)).ReturnsAsync((UploadFileEntity?)null);

        var act = () => CreateSut().Handle(
            new UploadFileCommand { FileId = id, FileName = "doc.txt", FileStream = MakeStream() }, default);

        await act.Should().ThrowAsync<BarkCloud.Shared.Exceptions.Files.FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_AlreadyUploaded_Throws()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Etag = "existing", Type = UploadFileType.CloudFile, Uploaders = new() { 1 } });

        var act = () => CreateSut().Handle(
            new UploadFileCommand { FileId = id, FileName = "doc.txt", FileStream = MakeStream() }, default);

        await act.Should().ThrowAsync<FileAlreadyUploadedException>();
    }

    [Fact]
    public async Task Handle_SameHashExists_StillUploadsAsIndependentCopy()
    {
        // Дедупликация отключена: одинаковый контент сохраняется как отдельный независимый блоб.
        var id = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        // Хеш уже присутствует в хранилище — раньше это приводило к дедупликации.
        _hashes.Setup(s => s.GetFileIdByHash(It.IsAny<string>())).ReturnsAsync(existingId);

        var response = await CreateSut().Handle(
            new UploadFileCommand { FileId = id, FileName = "doc.txt", FileStream = MakeStream() }, default);

        // Возвращается ID нового файла, а не существующего; новый блоб залит, его хеш сохранён.
        response.Should().Be(id.ToString());
        _s3.Verify(s => s.UploadAsync("test-bucket", id.ToString(), It.IsAny<Stream>(), "text/plain"), Times.Once);
        _hashes.Verify(s => s.AddHash(It.Is<FileHash>(h => h.FileId == id)), Times.Once);
        // Никакой дедупликации: не привязываем новый контент к существующему и не удаляем новую запись.
        _files.Verify(s => s.AddUploaderToFile(existingId, It.IsAny<long>()), Times.Never);
        _files.Verify(s => s.DeleteFile(id), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPathDocument_UploadsAndSavesHash()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _hashes.Setup(s => s.GetFileIdByHash(It.IsAny<string>())).ReturnsAsync((Guid?)null);

        var response = await CreateSut().Handle(
            new UploadFileCommand { FileId = id, FileName = "doc.txt", FileStream = MakeStream() }, default);

        response.Should().Be(id.ToString());
        _s3.Verify(
            s => s.UploadAsync("test-bucket", id.ToString(), It.IsAny<Stream>(), "text/plain"),
            Times.Once);
        _files.Verify(s => s.UpdateFile(It.Is<UploadFileEntity>(f => f.Etag == "etag-123" && f.Filename == "doc.txt")), Times.Once);
        _hashes.Verify(s => s.AddHash(It.Is<FileHash>(h => h.FileId == id)), Times.Once);
    }

    [Fact]
    public async Task Handle_HeicFile_ConvertsToJpegBeforeUpload()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _hashes.Setup(s => s.GetFileIdByHash(It.IsAny<string>())).ReturnsAsync((Guid?)null);

        var jpegBytes = System.Text.Encoding.UTF8.GetBytes("jpeg-bytes");
        _heicConverter
            .Setup(c => c.ConvertToJpegAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jpegBytes);

        var response = await CreateSut().Handle(
            new UploadFileCommand { FileId = id, FileName = "photo.heic", FileStream = MakeStream() }, default);

        response.Should().Be(id.ToString());
        _heicConverter.Verify(c => c.ConvertToJpegAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        // Оригинал заливается уже как JPEG, имя меняется на .jpg.
        _s3.Verify(
            s => s.UploadAsync("test-bucket", id.ToString(), It.IsAny<Stream>(), "image/jpeg"),
            Times.Once);
        _files.Verify(s => s.UpdateFile(It.Is<UploadFileEntity>(f => f.Filename == "photo.jpg")), Times.Once);
    }
}
