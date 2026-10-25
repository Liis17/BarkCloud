using System.Security.Cryptography;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetFileData;
using BarkCloud.Files.Features.UploadFile;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Tests.Services;

public sealed class ResumableAvatarPipelineTests
{
    [Fact]
    public async Task UserAvatar_IsEnrichedFromOriginalAndBecomesReadyWithItsType()
    {
        using var database = new SqliteFilesContext();
        using var profiles = new S3BucketRegistry(TestConfiguration.With(
            ("StorageProfiles:universal:ProfileId", "universal-v1"),
            ("StorageProfiles:universal:Role", "universal"),
            ("StorageProfiles:universal:Version", "1"),
            ("StorageProfiles:universal:ServiceUrl", "http://localhost:9000"),
            ("StorageProfiles:universal:AccessKey", "test"),
            ("StorageProfiles:universal:SecretKey", "test-secret"),
            ("StorageProfiles:universal:BucketName", "files"),
            ("StorageProfiles:universal:IsActive", "true"),
            ("StorageProfiles:avatars:ProfileId", "avatars-v1"),
            ("StorageProfiles:avatars:Role", "avatars"),
            ("StorageProfiles:avatars:Version", "1"),
            ("StorageProfiles:avatars:ServiceUrl", "http://localhost:9000"),
            ("StorageProfiles:avatars:AccessKey", "test"),
            ("StorageProfiles:avatars:SecretKey", "test-secret"),
            ("StorageProfiles:avatars:BucketName", "avatars"),
            ("StorageProfiles:avatars:IsActive", "true")));
        var originalBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4 };
        var originalHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        var quota = new Mock<IStorageQuotaService>();
        quota.Setup(x => x.GetSnapshotAsync(42, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        var multipart = new Mock<IMultipartUploadStore>();
        multipart.Setup(x => x.InitiateAsync(
                "avatars-v1", It.IsAny<string>(), "image/png", "avatar.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync("multipart-id");
        var coordinator = new UploadSessionCoordinator(
            database.Context, quota.Object, multipart.Object, Mock.Of<IUploadProcessingPublisher>(), profiles,
            TimeProvider.System, NullLogger<UploadSessionCoordinator>.Instance);

        var created = await coordinator.CreateAsync(42, "Android", new CreateUploadSessionInput(
            "avatar-upload", "avatar.png", originalBytes.Length, "image/png", originalHash, UploadFileType.UserAvatar), default);
        var session = await database.Context.UploadSessions.SingleAsync(x => x.Id == created.SessionId);
        session.Status = UploadSessionStatus.Processing;
        session.CompletedEtag = "uploaded-original-etag";
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var s3 = new Mock<S3Uploader>(profiles) { CallBase = false };
        s3.Setup(x => x.DownloadAsync("avatars-v1", created.FileId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(originalBytes, writable: false));
        var files = new UploadedFilesStorage(database.Context);
        var hashes = new FileHashesStorage(database.Context);
        var imageCompressor = new Mock<ImageCompressor>();
        imageCompressor.Setup(x => x.ProcessImageAllInOneAsync(
                It.IsAny<Stream>(), false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageProcessingResult(null, null, 2, 2));
        var metadataExtractor = new Mock<FileMetadataExtractor>(NullLogger<FileMetadataExtractor>.Instance);
        var previewPersistence = new PreviewPersistenceService(
            files, hashes, s3.Object, database.Context, NullLogger<PreviewPersistenceService>.Instance);
        var uploadHandler = new UploadFileCommandHandler(
            files, hashes, Mock.Of<IFileMetadataStorage>(), s3.Object, profiles,
            imageCompressor.Object,
            new Mock<VideoThumbnailExtractor>(NullLogger<VideoThumbnailExtractor>.Instance).Object,
            new Mock<HeicImageConverter>(NullLogger<HeicImageConverter>.Instance).Object,
            Mock.Of<ISvgRasterizer>(),
            metadataExtractor.Object, previewPersistence, NullLogger<UploadFileCommandHandler>.Instance);
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
            .Returns((UploadFileCommand command, CancellationToken ct) => uploadHandler.Handle(command, ct));
        var pipeline = new ExistingUploadEnrichmentPipeline(s3.Object, sender.Object);
        var processor = new UploadSessionProcessor(
            database.Context, pipeline, Mock.Of<IUploadArtifactCleaner>(), TimeProvider.System,
            NullLogger<UploadSessionProcessor>.Instance);

        (await processor.ProcessAsync(created.SessionId, default)).Should().Be(UploadProcessingOutcome.Ready);

        var savedFile = await files.GetFile(created.FileId);
        savedFile.Should().NotBeNull();
        savedFile!.Type.Should().Be(UploadFileType.UserAvatar);
        savedFile.Filename.Should().Be("avatar.png");
        savedFile.Size.Should().Be(originalBytes.Length);
        savedFile.Etag.Should().Be("uploaded-original-etag");
        savedFile.ImageWidth.Should().Be(2);
        (await hashes.GetHashByFileId(created.FileId))!.Hash.Should().Be(originalHash);
        (await coordinator.GetAsync(42, created.SessionId, default)).Status.Should().Be(UploadSessionStatus.Ready);

        var response = await new GetFileDataCommandHandler(
            files, new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty(),
            NullLogger<GetFileDataCommandHandler>.Instance)
            .Handle(new GetFileDataCommand { FileId = created.FileId, UserId = 0 }, default);
        response.FileInfo.Id.Should().Be(created.FileId.ToString());
        response.FileInfo.Type.Should().Be(BarkCloud.Proto.Files.UploadFileType.UserAvatar);
        response.FileInfo.FileSize.Should().Be(originalBytes.Length);
        response.FileInfo.FileName.Should().Be("avatar.png");

        s3.Verify(x => x.DownloadAsync("avatars-v1", created.FileId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
        s3.Verify(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
        (await database.Context.FilePreviews.ToListAsync()).Should().BeEmpty();
    }
}
