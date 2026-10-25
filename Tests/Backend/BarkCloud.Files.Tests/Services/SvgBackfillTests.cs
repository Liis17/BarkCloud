using System.Buffers.Binary;
using System.Text;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Services;

public sealed class SvgBackfillTests
{
    [Fact]
    public async Task LegacyPreviewBackfill_UsesRasterizedPngAndLeavesOriginalBlobUntouched()
    {
        var id = Guid.NewGuid();
        var original = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" />");
        var png = Png(100, 50);
        var file = ReadySvg(id, original.Length);
        var deps = CreateDependencies(file);
        var compressorInputs = new List<byte[]>();
        deps.S3.Setup(x => x.DownloadAsync("read-profile", id.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(original, writable: false));
        deps.Rasterizer.Setup(x => x.RasterizeBackfillAsync(It.IsAny<Stream>(), "vector.svg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(png);
        deps.Compressor.Setup(x => x.ProcessImageAllInOneAsync(
                It.IsAny<Stream>(), false, null, It.IsAny<CancellationToken>()))
            .Callback<Stream, bool, int?, CancellationToken>((stream, _, _, _) => compressorInputs.Add(ReadFromStart(stream)))
            .ReturnsAsync(new ImageProcessingResult(null, null, 100, 50));
        var previews = new List<MultiPreviewItem> { new(128, 100, 50, [1, 2, 3]) };
        deps.Compressor.Setup(x => x.GenerateMultiplePreviewsAsync(
                It.IsAny<Stream>(), It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, int[], CancellationToken>((stream, _, _) => compressorInputs.Add(ReadFromStart(stream)))
            .ReturnsAsync(previews);
        deps.PreviewPersistence.Setup(x => x.PersistPreviewsAsync(
                It.IsAny<UploadFileEntity>(), previews, "preview-profile", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var provider = deps.Services;
        await new LegacyPreviewBackfillService(
            Mock.Of<IServiceScopeFactory>(), NullLogger<LegacyPreviewBackfillService>.Instance)
            .ProcessFileAsync(provider, id, default);

        compressorInputs.Should().HaveCount(2).And.OnlyContain(bytes => bytes.SequenceEqual(png));
        deps.Rasterizer.Verify(x => x.RasterizeBackfillAsync(It.IsAny<Stream>(), "vector.svg", It.IsAny<CancellationToken>()), Times.Once);
        deps.S3.Verify(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
        deps.Files.Verify(x => x.UpdateFile(It.Is<UploadFileEntity>(saved =>
            saved.Filename == "vector.svg" && saved.Size == original.Length && saved.Etag == "original-etag"
            && saved.ImageWidth == 100 && saved.ImageHeight == 50)), Times.Once);
    }

    [Fact]
    public async Task LegacyJpegViewBackfill_EncodesRasterPngAsJpegAndLeavesOriginalBlobUntouched()
    {
        var id = Guid.NewGuid();
        var original = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" />");
        var png = Png(80, 40);
        var jpeg = Encoding.UTF8.GetBytes("jpeg-view");
        var file = ReadySvg(id, original.Length);
        var deps = CreateDependencies(file);
        var encodedInput = Array.Empty<byte>();
        deps.S3.Setup(x => x.DownloadAsync("read-profile", id.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(original, writable: false));
        deps.Rasterizer.Setup(x => x.RasterizeBackfillAsync(It.IsAny<Stream>(), "vector.svg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(png);
        deps.Compressor.Setup(x => x.EncodeFullJpegAsync(
                It.IsAny<Stream>(), 90, It.IsAny<CancellationToken>()))
            .Callback<Stream, int, CancellationToken>((stream, _, _) => encodedInput = ReadFromStart(stream))
            .ReturnsAsync(jpeg);
        var viewId = Guid.NewGuid();
        deps.PreviewPersistence.Setup(x => x.PersistJpegViewAsync(
                It.IsAny<UploadFileEntity>(), jpeg, It.IsAny<int>(), It.IsAny<int>(), "preview-profile", It.IsAny<CancellationToken>()))
            .ReturnsAsync((viewId, (byte[]?)jpeg));
        deps.PreviewPersistence.Setup(x => x.EnsurePlaceholderAsync(
                It.IsAny<UploadFileEntity>(), It.IsAny<IReadOnlyDictionary<Guid, byte[]>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var provider = deps.Services;
        await new LegacyJpegViewBackfillService(
            Mock.Of<IServiceScopeFactory>(), NullLogger<LegacyJpegViewBackfillService>.Instance)
            .ProcessFileAsync(provider, id, default);

        encodedInput.Should().BeEquivalentTo(png);
        deps.PreviewPersistence.Verify(x => x.PersistJpegViewAsync(
            It.IsAny<UploadFileEntity>(), jpeg, It.IsAny<int>(), It.IsAny<int>(), "preview-profile", It.IsAny<CancellationToken>()), Times.Once);
        deps.S3.Verify(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
        deps.Files.Verify(x => x.UpdateFile(It.Is<UploadFileEntity>(saved =>
            saved.Filename == "vector.svg" && saved.Size == original.Length && saved.Etag == "original-etag"
            && saved.JpegViewFileId == viewId)), Times.Once);
    }

    [Theory]
    [InlineData(false, 50)]
    [InlineData(true, 2097153)]
    public async Task Backfills_SkipUnreadyOrOversizedSvgBeforeDownloading(bool ready, long size)
    {
        var id = Guid.NewGuid();
        var file = ReadySvg(id, size);
        if (!ready)
            file.UploadedAt = null;
        var deps = CreateDependencies(file);
        await using var provider = deps.Services;

        await new LegacyPreviewBackfillService(
            Mock.Of<IServiceScopeFactory>(), NullLogger<LegacyPreviewBackfillService>.Instance)
            .ProcessFileAsync(provider, id, default);
        await new LegacyJpegViewBackfillService(
            Mock.Of<IServiceScopeFactory>(), NullLogger<LegacyJpegViewBackfillService>.Instance)
            .ProcessFileAsync(provider, id, default);

        deps.S3.Verify(x => x.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        deps.Rasterizer.Verify(x => x.RasterizeBackfillAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static UploadFileEntity ReadySvg(Guid id, long size) => new()
    {
        Id = id,
        Type = UploadFileType.CloudFile,
        MediaKind = MediaKind.Photo,
        Filename = "vector.svg",
        Size = size,
        UploadedAt = DateTime.UtcNow,
        Etag = "original-etag",
    };

    private static Dependencies CreateDependencies(UploadFileEntity file)
    {
        var files = new Mock<IUploadedFilesStorage>();
        files.Setup(x => x.GetFile(file.Id)).ReturnsAsync(file);
        files.Setup(x => x.UpdateFile(It.IsAny<UploadFileEntity>())).Returns(Task.CompletedTask);
        var hashes = new Mock<IFileHashesStorage>();
        var metadata = new Mock<IFileMetadataStorage>();
        var bucketRegistry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
        bucketRegistry.Setup(x => x.ResolveReadProfileId(It.IsAny<UploadFileEntity>())).Returns("read-profile");
        bucketRegistry.Setup(x => x.ResolveWriteProfileId(UploadFileType.CloudFile, MediaKind.Photo, true)).Returns("preview-profile");
        var s3 = new Mock<S3Uploader>(bucketRegistry.Object) { CallBase = false };
        var compressor = new Mock<ImageCompressor>();
        var heic = new Mock<HeicImageConverter>(NullLogger<HeicImageConverter>.Instance);
        var rasterizer = new Mock<ISvgRasterizer>();
        var previewPersistence = new Mock<PreviewPersistenceService>(
            files.Object, hashes.Object, s3.Object, /* FilesContext */ null!,
            NullLogger<PreviewPersistenceService>.Instance, null!);
        var services = new ServiceCollection()
            .AddSingleton<IUploadedFilesStorage>(files.Object)
            .AddSingleton<IFileHashesStorage>(hashes.Object)
            .AddSingleton<IFileMetadataStorage>(metadata.Object)
            .AddSingleton(bucketRegistry.Object)
            .AddSingleton(s3.Object)
            .AddSingleton(compressor.Object)
            .AddSingleton(heic.Object)
            .AddSingleton(rasterizer.Object)
            .AddSingleton(previewPersistence.Object)
            .BuildServiceProvider();

        return new Dependencies(services, files, s3, compressor, rasterizer, previewPersistence);
    }

    private static byte[] ReadFromStart(Stream stream)
    {
        var position = stream.Position;
        stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        stream.Position = position;
        return copy.ToArray();
    }

    private static byte[] Png(uint width, uint height)
    {
        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(png, 12);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), height);
        return png;
    }

    private sealed record Dependencies(
        ServiceProvider Services,
        Mock<IUploadedFilesStorage> Files,
        Mock<S3Uploader> S3,
        Mock<ImageCompressor> Compressor,
        Mock<ISvgRasterizer> Rasterizer,
        Mock<PreviewPersistenceService> PreviewPersistence);
}
