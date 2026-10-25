using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Features.UploadFile;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

using System.Security.Cryptography;
using System.Text;

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
    private readonly Mock<ISvgRasterizer> _svgRasterizer = new();
    private readonly Mock<FileMetadataExtractor> _metadataExtractor;
    private readonly Mock<PreviewPersistenceService> _previewPersistence;

    public UploadFileCommandHandlerTests()
    {
        _bucketRegistry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
        _bucketRegistry.Setup(r => r.GetBucketName(It.IsAny<UploadFileType>())).Returns("test-bucket");
        _bucketRegistry.Setup(r => r.ResolveWriteProfileId(
                It.IsAny<UploadFileType>(), It.IsAny<MediaKind>(), It.IsAny<bool>()))
            .Returns("test-bucket");

        _s3 = new Mock<S3Uploader>(_bucketRegistry.Object) { CallBase = false };
        _s3.Setup(u => u.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync("etag-123");

        _imageCompressor = new Mock<ImageCompressor>();
        _videoExtractor = new Mock<VideoThumbnailExtractor>(NullLogger<VideoThumbnailExtractor>.Instance);
        _heicConverter = new Mock<HeicImageConverter>(NullLogger<HeicImageConverter>.Instance);
        _metadataExtractor = new Mock<FileMetadataExtractor>(NullLogger<FileMetadataExtractor>.Instance);
        _previewPersistence = new Mock<PreviewPersistenceService>(
            _files.Object, _hashes.Object, _s3.Object, /* FilesContext */ null!,
            NullLogger<PreviewPersistenceService>.Instance, null!);
    }

    private UploadFileCommandHandler CreateSut(IUploadTempFileProvider? tempFiles = null) => new(
        _files.Object, _hashes.Object, _metadata.Object, _s3.Object, _bucketRegistry.Object,
        _imageCompressor.Object, _videoExtractor.Object, _heicConverter.Object,
        _svgRasterizer.Object, _metadataExtractor.Object, _previewPersistence.Object,
        NullLogger<UploadFileCommandHandler>.Instance,
        tempFiles: tempFiles);

    private static Stream MakeStream(string content = "hello") => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

    private static byte[] ReadFromStart(Stream stream)
    {
        var position = stream.Position;
        stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        stream.Position = position;
        return copy.ToArray();
    }

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
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });

        var response = await CreateSut().Handle(
            new UploadFileCommand { FileId = id, FileName = "doc.txt", FileStream = MakeStream() }, default);

        // Загружается новый блоб со своим id, его хеш сохраняется; никакого дедупа.
        response.Should().Be(id.ToString());
        _s3.Verify(s => s.UploadAsync("test-bucket", id.ToString(), It.IsAny<Stream>(), "text/plain"), Times.Once);
        _hashes.Verify(s => s.AddHash(It.Is<FileHash>(h => h.FileId == id)), Times.Once);
        // Не привязываем новый контент к существующему и не удаляем новую запись.
        _files.Verify(s => s.AddUploaderToFile(It.IsAny<Guid>(), It.IsAny<long>()), Times.Never);
        _files.Verify(s => s.DeleteFile(id), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPathDocument_UploadsAndSavesHash()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });

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
    public async Task Handle_JpegFile_GeneratesSeparateJpegViewBlob()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });

        var viewBytes = System.Text.Encoding.UTF8.GetBytes("jpeg-view");
        _imageCompressor
            .Setup(c => c.EncodeFullJpegAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(viewBytes);

        var viewId = Guid.NewGuid();
        _previewPersistence
            .Setup(p => p.PersistJpegViewAsync(
                It.IsAny<UploadFileEntity>(), It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((viewId, (byte[]?)viewBytes));

        var response = await CreateSut().Handle(
            new UploadFileCommand { FileId = id, FileName = "photo.jpg", FileStream = MakeStream() }, default);

        response.Should().Be(id.ToString());
        // JPEG-оригинал тоже перекодируется в отдельный полноразмерный JPEG-блоб (не ссылается на себя),
        // иначе JpegViewUrl указывал бы на /download/{оригинал}, который запрещён для CloudFile → 404.
        _previewPersistence.Verify(p => p.PersistJpegViewAsync(
            It.IsAny<UploadFileEntity>(), viewBytes, It.IsAny<int>(), It.IsAny<int>(), "test-bucket", It.IsAny<CancellationToken>()),
            Times.Once);
        _files.Verify(s => s.UpdateFile(It.Is<UploadFileEntity>(f => f.JpegViewFileId == viewId)), Times.Once);
        _previewPersistence.Verify(p => p.EnsurePlaceholderAsync(
            It.IsAny<UploadFileEntity>(),
            It.Is<IReadOnlyDictionary<Guid, byte[]>?>(bytes => bytes != null && bytes.ContainsKey(viewId) && bytes[viewId] == viewBytes),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_SvgCloudFile_RasterizesDerivedImagesAndPreservesOriginalAndHash()
    {
        var id = Guid.NewGuid();
        var original = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" />");
        var raster = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        var jpeg = Encoding.UTF8.GetBytes("jpeg-view");
        var rasterizerInput = Array.Empty<byte>();
        var s3Original = Array.Empty<byte>();
        var imageInputs = new List<byte[]>();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _svgRasterizer.Setup(x => x.RasterizeUploadAsync(It.IsAny<Stream>(), "vector.svg", It.IsAny<CancellationToken>()))
            .Callback<Stream, string, CancellationToken>((stream, _, _) => rasterizerInput = ReadFromStart(stream))
            .ReturnsAsync(raster);
        _s3.Setup(s => s.UploadAsync("test-bucket", id.ToString(), It.IsAny<Stream>(), "image/svg+xml"))
            .Callback<string, string, Stream, string>((_, _, stream, _) =>
            {
                s3Original = ReadFromStart(stream);
                stream.Position = 0;
            })
            .ReturnsAsync("svg-etag");
        _imageCompressor.Setup(x => x.ProcessImageAllInOneAsync(
                It.IsAny<Stream>(), false, null, It.IsAny<CancellationToken>()))
            .Callback<Stream, bool, int?, CancellationToken>((stream, _, _, _) => imageInputs.Add(ReadFromStart(stream)))
            .ReturnsAsync(new ImageProcessingResult(null, null, 12, 8));
        var previews = new List<MultiPreviewItem> { new(1024, 12, 8, [1, 2, 3]) };
        _imageCompressor.Setup(x => x.GenerateMultiplePreviewsAsync(
                It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, int[], CancellationToken>((stream, _, _) => imageInputs.Add(ReadFromStart(stream)))
            .ReturnsAsync(previews);
        _imageCompressor.Setup(x => x.EncodeFullJpegAsync(
                It.IsAny<Stream>(), 90, It.IsAny<CancellationToken>()))
            .Callback<Stream, int, CancellationToken>((stream, _, _) => imageInputs.Add(ReadFromStart(stream)))
            .ReturnsAsync(jpeg);
        var viewId = Guid.NewGuid();
        _previewPersistence.Setup(x => x.PersistJpegViewAsync(
                It.IsAny<UploadFileEntity>(), jpeg, It.IsAny<int>(), It.IsAny<int>(), "test-bucket", It.IsAny<CancellationToken>()))
            .ReturnsAsync((viewId, (byte[]?)jpeg));
        _previewPersistence.Setup(x => x.PersistPreviewsAsync(
                It.IsAny<UploadFileEntity>(), previews, "test-bucket", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var response = await CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "vector.svg",
            ContentTypeOverride = "image/svg+xml",
            FileStream = new MemoryStream(original),
        }, default);

        response.Should().Be(id.ToString());
        rasterizerInput.Should().BeEquivalentTo(original);
        s3Original.Should().BeEquivalentTo(original);
        imageInputs.Should().HaveCount(3).And.OnlyContain(bytes => bytes.SequenceEqual(raster));
        _files.Verify(x => x.UpdateFile(It.Is<UploadFileEntity>(file =>
            file.Id == id && file.Filename == "vector.svg" && file.Size == original.Length
            && file.Etag == "svg-etag" && file.JpegViewFileId == viewId)), Times.Once);
        _hashes.Verify(x => x.AddHash(It.Is<FileHash>(hash =>
            hash.FileId == id && hash.Hash == Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant())), Times.Once);
        _svgRasterizer.Verify(x => x.RasterizeUploadAsync(It.IsAny<Stream>(), "vector.svg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_SvgRasterizationFailure_DoesNotFailDeferredUploadOrGeneratePreview()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _svgRasterizer.Setup(x => x.RasterizeUploadAsync(It.IsAny<Stream>(), "broken.svg", It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var result = await CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "broken.svg",
            ContentTypeOverride = "image/svg+xml",
            FileStream = MakeStream("<svg"),
            DeferAvailability = true,
        }, default);

        result.Should().Be(id.ToString());
        _imageCompressor.Verify(x => x.ProcessImageAllInOneAsync(
            It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        _imageCompressor.Verify(x => x.GenerateMultiplePreviewsAsync(
            It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()), Times.Never);
        _files.Verify(x => x.UpdateFile(It.Is<UploadFileEntity>(file =>
            file.Etag == "etag-123" && file.UploadedAt == null && file.Filename == "broken.svg")), Times.Once);
    }

    [Fact]
    public async Task Handle_SvgMimeWithoutSvgExtension_IsRasterizedAndClassifiedAsPhoto()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _svgRasterizer.Setup(x => x.RasterizeUploadAsync(It.IsAny<Stream>(), "vector.bin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        await CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "vector.bin",
            ContentTypeOverride = "image/svg+xml; charset=utf-8",
            FileStream = MakeStream("<svg"),
        }, default);

        _svgRasterizer.Verify(x => x.RasterizeUploadAsync(It.IsAny<Stream>(), "vector.bin", It.IsAny<CancellationToken>()), Times.Once);
        _files.Verify(x => x.UpdateFile(It.Is<UploadFileEntity>(file =>
            file.MediaKind == MediaKind.Photo && file.Filename == "vector.bin" && file.Etag == "etag-123")), Times.Once);
    }

    [Fact]
    public async Task Handle_SvgPngDecodeFailure_DoesNotFailDeferredUpload()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _svgRasterizer.Setup(x => x.RasterizeUploadAsync(It.IsAny<Stream>(), "broken.svg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2, 3]);
        _imageCompressor.Setup(x => x.ProcessImageAllInOneAsync(
                It.IsAny<Stream>(), false, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("invalid PNG"));
        _imageCompressor.Setup(x => x.EncodeFullJpegAsync(
                It.IsAny<Stream>(), 90, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("invalid PNG"));

        var result = await CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "broken.svg",
            ContentTypeOverride = "image/svg+xml",
            FileStream = MakeStream("<svg"),
            DeferAvailability = true,
        }, default);

        result.Should().Be(id.ToString());
        _files.Verify(x => x.UpdateFile(It.Is<UploadFileEntity>(file =>
            file.Etag == "etag-123" && file.UploadedAt == null && file.Filename == "broken.svg")), Times.Once);
        _previewPersistence.Verify(x => x.PersistJpegViewAsync(
            It.IsAny<UploadFileEntity>(), It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("image/heic")]
    [InlineData("image/heif")]
    public async Task Handle_HeicFile_KeepsOriginalAndGeneratesJpegView(string contentType)
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });

        var jpegBytes = System.Text.Encoding.UTF8.GetBytes("jpeg-bytes");
        _heicConverter
            .Setup(c => c.ConvertToJpegAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jpegBytes);

        var response = await CreateSut().Handle(
            new UploadFileCommand
            {
                FileId = id,
                FileName = "photo.heic",
                ContentTypeOverride = contentType,
                FileStream = MakeStream()
            }, default);

        response.Should().Be(id.ToString());
        // HEIC конвертируется в JPEG-представление (для превью/просмотра в вебе).
        _heicConverter.Verify(c => c.ConvertToJpegAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        // Но оригинал заливается КАК ЕСТЬ (image/heic, имя без изменений) — чтобы его SHA256
        // совпадал с клиентским и не ломались дедуп и индикатор «уже в облаке».
        _s3.Verify(
            s => s.UploadAsync("test-bucket", id.ToString(), It.IsAny<Stream>(), contentType),
            Times.Once);
        _files.Verify(s => s.UpdateFile(It.Is<UploadFileEntity>(f => f.Filename == "photo.heic")), Times.Once);
    }

    [Fact]
    public async Task Handle_VideoFile_UsesLandscapeVideoPreviewGenerator()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });

        _videoExtractor
            .Setup(v => v.ProbeFullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoProbe(
                Width: 1080,
                Height: 1920,
                Duration: TimeSpan.FromSeconds(10),
                VideoCodec: "h264",
                AudioCodec: "aac",
                BitRate: 1_000_000,
                FrameRate: 30,
                FormatTags: null));
        _videoExtractor
            .Setup(v => v.ExtractFrameJpegAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });

        var previews = new List<MultiPreviewItem>
        {
            new(1024, 1024, 576, new byte[] { 4, 5, 6 })
        };
        _imageCompressor
            .Setup(c => c.GenerateVideoPreviewsAsync(
                It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(previews);
        _previewPersistence
            .Setup(p => p.PersistPreviewsAsync(
                It.IsAny<UploadFileEntity>(), previews, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var response = await CreateSut().Handle(
            new UploadFileCommand { FileId = id, FileName = "clip.mp4", FileStream = MakeStream() }, default);

        response.Should().Be(id.ToString());
        _imageCompressor.Verify(c => c.GenerateVideoPreviewsAsync(
            It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()), Times.Once);
        _imageCompressor.Verify(c => c.GenerateMultiplePreviewsAsync(
            It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()), Times.Never);
        _previewPersistence.Verify(p => p.PersistPreviewsAsync(
            It.IsAny<UploadFileEntity>(), previews, "test-bucket", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_DeferredVideo_WhenThumbnailFails_StillCompletesEnrichment()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _videoExtractor
            .Setup(v => v.ProbeFullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoProbe(
                Width: 1920,
                Height: 1080,
                Duration: TimeSpan.FromSeconds(10),
                VideoCodec: "h264",
                AudioCodec: "aac",
                BitRate: 1_000_000,
                FrameRate: 30,
                FormatTags: null));
        _videoExtractor
            .Setup(v => v.ExtractFrameJpegAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("thumbnail failed"));

        var response = await CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "clip.mp4",
            FileStream = MakeStream(),
            DeferAvailability = true
        }, default);

        response.Should().Be(id.ToString());
        _files.Verify(s => s.UpdateFile(It.Is<UploadFileEntity>(f =>
            f.Id == id && f.Etag == "etag-123" && f.UploadedAt == null)), Times.Once);
        _previewPersistence.Verify(p => p.PersistPreviewsAsync(
            It.IsAny<UploadFileEntity>(), It.IsAny<List<MultiPreviewItem>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeferredUpload_UsesSessionContentTypeForKnownFormatValidation()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _videoExtractor
            .Setup(v => v.ProbeFullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("not a video"));

        var act = () => CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "opaque.bin",
            ContentTypeOverride = "video/mp4",
            FileStream = MakeStream(),
            DeferAvailability = true
        }, default);

        await act.Should().ThrowAsync<FileIntegrityException>();
        _videoExtractor.Verify(v => v.ProbeFullAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenActualSizeDiffersFromDeclaredSize_RejectsObject()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });

        var act = () => CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "doc.txt",
            FileStream = MakeStream("hello"),
            FileSize = 6,
            DeferAvailability = true
        }, default);

        await act.Should().ThrowAsync<FileIntegrityException>()
            .WithMessage("*размер*");
        _s3.Verify(s => s.UploadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenSha256DiffersFromDeclaredHash_RejectsObject()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });

        var act = () => CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "doc.txt",
            FileStream = MakeStream("hello"),
            FileSize = 5,
            ExpectedSha256 = new string('0', 64),
            DeferAvailability = true
        }, default);

        await act.Should().ThrowAsync<FileIntegrityException>()
            .WithMessage("*SHA-256*");
        _s3.Verify(s => s.UploadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeferredPdf_WhenDocumentCannotBeParsed_RejectsObject()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _metadataExtractor.Setup(x => x.ExtractFromPdf(It.IsAny<Stream>(), rejectInvalid: true))
            .Throws(new InvalidDataException("not a PDF"));

        var act = () => CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "document.pdf",
            FileStream = MakeStream("not a PDF"),
            DeferAvailability = true
        }, default);

        await act.Should().ThrowAsync<FileIntegrityException>()
            .WithMessage("*документ*");
        _s3.Verify(s => s.UploadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeferredImage_WhenPreviewPersistenceFails_StillCompletesEnrichment()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        _imageCompressor.Setup(x => x.ProcessImageAllInOneAsync(
                It.IsAny<Stream>(), false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageProcessingResult(null, null, 1920, 1080));
        var previews = new List<MultiPreviewItem>
        {
            new(1024, 1024, 576, [1, 2, 3])
        };
        _imageCompressor.Setup(x => x.GenerateMultiplePreviewsAsync(
                It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(previews);
        _previewPersistence.Setup(x => x.PersistPreviewsAsync(
                It.IsAny<UploadFileEntity>(), previews, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("preview storage unavailable"));

        var response = await CreateSut().Handle(new UploadFileCommand
        {
            FileId = id,
            FileName = "photo.jpg",
            FileStream = MakeStream(),
            DeferAvailability = true
        }, default);

        response.Should().Be(id.ToString());
        _files.Verify(x => x.UpdateFile(It.Is<UploadFileEntity>(file =>
            file.Id == id && file.Etag == "etag-123" && file.UploadedAt == null)), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenDiskBufferCopyFails_DeletesTemporaryFile()
    {
        var id = Guid.NewGuid();
        _files.Setup(s => s.GetFile(id))
            .ReturnsAsync(new UploadFileEntity { Id = id, Type = UploadFileType.CloudFile, Uploaders = new() { 42 } });
        var path = Path.Combine(Path.GetTempPath(), $"barkcloud-test-{Guid.NewGuid():N}.tmp");
        var tempFiles = new Mock<IUploadTempFileProvider>();
        tempFiles.Setup(x => x.CreatePath()).Returns(path);

        try
        {
            var act = () => CreateSut(tempFiles.Object).Handle(new UploadFileCommand
            {
                FileId = id,
                FileName = "file.bin",
                FileStream = new FailingReadStream(),
                FileSize = 42,
                ForceDiskBuffer = true
            }, default);

            await act.Should().ThrowAsync<IOException>();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("copy failed");
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => Task.FromException<int>(new IOException("copy failed"));
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("copy failed"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
