using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
using BarkCloud.Files.Features.UploadFile;
using BarkCloud.Files.Host;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Metrics;

using MediatR;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Host;

public class FilesControllerTests
{
    [Fact]
    public async Task UploadFile_WhenClientDisconnectsAfterHandler_StillFinalizesReservation()
    {
        using var database = new SqliteFilesContext();
        var fileId = Guid.NewGuid();
        database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await database.Context.SaveChangesAsync();
        var quota = new Mock<IStorageQuotaService>();
        quota.Setup(x => x.GetSnapshotAsync(42, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        var guard = new LegacyUploadQuotaGuard(database.Context, quota.Object, TimeProvider.System);
        using var disconnect = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async (UploadFileCommand command, CancellationToken _) =>
            {
                var file = database.Context.UploadedFiles.Single(x => x.Id == fileId);
                file.Etag = "etag";
                file.Size = 3;
                await database.Context.SaveChangesAsync();
                await guard.MarkProcessingCompletedAsync(
                    command.QuotaReservationId!.Value, CancellationToken.None);
                disconnect.Cancel();
                return fileId.ToString();
            });
        var controller = CreateController(database, quota, guard, mediator.Object);
        controller.HttpContext.RequestAborted = disconnect.Token;
        await using var contents = new MemoryStream([1, 2, 3]);
        var formFile = new FormFile(contents, 0, contents.Length, "file", "legacy.bin");

        var result = await controller.UploadFile(fileId, formFile);

        result.Should().BeOfType<OkObjectResult>();
        database.Context.UploadSessions.Single().Status.Should().Be(UploadSessionStatus.Ready);
        database.Context.UploadSessions.Single().ReservedBytes.Should().Be(0);
        database.Context.UploadedFiles.Single().UploadedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UploadFile_WhenPreviousRequestPersistedOriginal_CompletesWithoutProcessingAgain()
    {
        using var database = new SqliteFilesContext();
        var fileId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            Filename = "legacy.bin",
            Size = 3,
            Etag = "etag",
            UploadedAt = null,
            CreatedAt = now,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        database.Context.UploadSessions.Add(new UploadSession
        {
            Id = Guid.NewGuid(),
            FileId = fileId,
            OwnerId = 42,
            IdempotencyKey = $"legacy:{fileId:N}",
            FileName = "legacy.bin",
            DeclaredSize = 3,
            ContentType = "application/octet-stream",
            Sha256 = string.Empty,
            Status = UploadSessionStatus.Processing,
            StorageProfileId = "universal-v1",
            MultipartUploadId = string.Empty,
            PartSize = 3,
            UploadTokenHash = string.Empty,
            ReservedBytes = 3,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now,
            ExpiresAt = now.AddHours(24),
            LegacyProcessingCompletedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        });
        await database.Context.SaveChangesAsync();

        var quota = new Mock<IStorageQuotaService>();
        var guard = new LegacyUploadQuotaGuard(database.Context, quota.Object, TimeProvider.System);
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var controller = CreateController(database, quota, guard, mediator.Object);
        await using var contents = new MemoryStream([1, 2, 3]);
        var formFile = new FormFile(contents, 0, contents.Length, "file", "legacy.bin");

        var result = await controller.UploadFile(fileId, formFile);

        result.Should().BeOfType<OkObjectResult>();
        mediator.Verify(
            x => x.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var session = database.Context.UploadSessions.Single();
        session.Status.Should().Be(UploadSessionStatus.Ready);
        session.ReservedBytes.Should().Be(0);
        database.Context.UploadedFiles.Single().UploadedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UploadFile_WhenHandlerRejectsUpload_ReleasesLegacyReservation()
    {
        using var database = new SqliteFilesContext();
        var fileId = Guid.NewGuid();
        database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = [42],
            CreatedAt = DateTime.UtcNow,
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1"
        });
        await database.Context.SaveChangesAsync();

        var quota = new Mock<IStorageQuotaService>();
        quota.Setup(x => x.GetSnapshotAsync(42, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        var guard = new LegacyUploadQuotaGuard(database.Context, quota.Object, TimeProvider.System);
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileAlreadyUploadedException("already uploaded"));

        var controller = CreateController(database, quota, guard, mediator.Object);
        await using var contents = new MemoryStream([1, 2, 3]);
        var formFile = new FormFile(contents, 0, contents.Length, "file", "legacy.bin");

        var result = await controller.UploadFile(fileId, formFile);

        result.Should().BeOfType<BadRequestObjectResult>();
        var session = database.Context.UploadSessions.Single();
        session.Status.Should().Be(UploadSessionStatus.Failed);
        session.ReservedBytes.Should().Be(0);
    }

    private static FilesController CreateController(
        SqliteFilesContext database,
        Mock<IStorageQuotaService> quota,
        LegacyUploadQuotaGuard guard,
        IMediator mediator)
    {
        var objects = new Mock<IMultipartUploadStore>();
        var processing = new Mock<IUploadProcessingPublisher>();
        var profiles = new S3BucketRegistry(TestConfiguration.With(
            ("StorageProfiles:universal:ProfileId", "universal-v1"),
            ("StorageProfiles:universal:Role", "universal"),
            ("StorageProfiles:universal:Version", "1"),
            ("StorageProfiles:universal:ServiceUrl", "http://localhost:9000"),
            ("StorageProfiles:universal:AccessKey", "test"),
            ("StorageProfiles:universal:SecretKey", "test-secret"),
            ("StorageProfiles:universal:BucketName", "files"),
            ("StorageProfiles:universal:IsActive", "true")));
        var coordinator = new UploadSessionCoordinator(
            database.Context, quota.Object, objects.Object, processing.Object,
            profiles, TimeProvider.System, NullLogger<UploadSessionCoordinator>.Instance);
        return new FilesController(mediator, new MetricsCollector(), coordinator, guard)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }
}
