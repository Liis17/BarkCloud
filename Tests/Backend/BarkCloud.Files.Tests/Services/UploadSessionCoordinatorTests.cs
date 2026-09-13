using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public class UploadSessionCoordinatorTests : IDisposable
{
    private readonly SqliteFilesContext _database = new();
    private readonly Mock<IStorageLimitProvider> _limits = new();
    private readonly Mock<IMultipartUploadStore> _objects = new();
    private readonly Mock<IUploadProcessingPublisher> _processing = new();
    private readonly S3BucketRegistry _profiles;
    private readonly TimeProvider _time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero));

    public UploadSessionCoordinatorTests()
    {
        _profiles = new S3BucketRegistry(TestConfiguration.With(
            ("StorageProfiles:universal:ProfileId", "universal-v1"),
            ("StorageProfiles:universal:Role", "universal"),
            ("StorageProfiles:universal:Version", "1"),
            ("StorageProfiles:universal:ServiceUrl", "http://localhost:9000"),
            ("StorageProfiles:universal:AccessKey", "test"),
            ("StorageProfiles:universal:SecretKey", "test-secret"),
            ("StorageProfiles:universal:BucketName", "files"),
            ("StorageProfiles:universal:IsActive", "true")));
        _limits.Setup(x => x.GetLimitBytesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);
        _objects.Setup(x => x.InitiateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task CreateAsync_WithSameOwnerKeyAndDescriptor_ReturnsExistingSession()
    {
        var sut = CreateSut();
        var input = Input("upload-1", size: 42);

        var first = await sut.CreateAsync(10, "Safari", input, default);
        var repeated = await sut.CreateAsync(10, "Safari", input, default);

        repeated.SessionId.Should().Be(first.SessionId);
        repeated.FileId.Should().Be(first.FileId);
        repeated.Status.Should().Be(UploadSessionStatus.Uploading);
    }

    [Fact]
    public async Task CreateAsync_WithSameOwnerKeyAndDifferentDescriptor_RejectsConflict()
    {
        var sut = CreateSut();
        await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);

        var act = () => sut.CreateAsync(10, "Safari", Input("upload-1", size: 43), default);

        await act.Should().ThrowAsync<UploadIdempotencyConflictException>();
    }

    [Fact]
    public async Task CreateAsync_WithSameKeyForDifferentOwners_CreatesIndependentSessions()
    {
        var sut = CreateSut();

        var first = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        var second = await sut.CreateAsync(11, "Firefox", Input("upload-1", size: 42), default);

        second.SessionId.Should().NotBe(first.SessionId);
        second.FileId.Should().NotBe(first.FileId);
    }

    [Fact]
    public async Task CreateAsync_AboveS3MaximumObjectSize_RejectsBeforeInitiatingMultipart()
    {
        var act = () => CreateSut().CreateAsync(
            10,
            "Safari",
            Input("too-large", UploadSessionCoordinator.MaximumObjectSize + 1),
            default);

        await act.Should().ThrowAsync<InvalidUploadSessionRequestException>();
        _objects.Verify(x => x.InitiateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAsync_DoesNotExposeAnotherOwnersSession()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);

        var act = () => sut.GetAsync(11, created.SessionId, default);

        await act.Should().ThrowAsync<UploadSessionNotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_WhenReadyBytesAndRequestExceedLimit_ReportsQuotaDetails()
    {
        _limits.Setup(x => x.GetLimitBytesAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(100L);
        _database.Context.UploadedFiles.Add(new UploadFile
        {
            Id = Guid.NewGuid(),
            Uploaders = [10],
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            UploadedAt = _time.GetUtcNow().UtcDateTime,
            Etag = "etag",
            Type = UploadFileType.CloudFile,
            StorageProfileId = "universal-v1",
            Filename = "ready.bin",
            Size = 80
        });
        await _database.Context.SaveChangesAsync();

        var act = () => CreateSut().CreateAsync(10, "Safari", Input("upload-1", size: 21), default);

        var error = await act.Should().ThrowAsync<UploadQuotaExceededException>();
        error.Which.UsedBytes.Should().Be(80);
        error.Which.ReservedBytes.Should().Be(0);
        error.Which.RequestedBytes.Should().Be(21);
        error.Which.LimitBytes.Should().Be(100);
    }

    [Fact]
    public async Task CreateAsync_CountsExistingUploadReservations()
    {
        _limits.Setup(x => x.GetLimitBytesAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(100L);
        var sut = CreateSut();
        await sut.CreateAsync(10, "Safari", Input("upload-1", size: 60), default);

        var act = () => sut.CreateAsync(10, "Safari", Input("upload-2", size: 41), default);

        var error = await act.Should().ThrowAsync<UploadQuotaExceededException>();
        error.Which.UsedBytes.Should().Be(0);
        error.Which.ReservedBytes.Should().Be(60);
    }

    [Fact]
    public async Task UploadPartAsync_WithValidTokenAndRange_StreamsExpectedPart()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.UploadPartAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), 1,
                It.IsAny<Stream>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartUploadPart(1, 42, "part-etag"));

        await using var body = new MemoryStream(new byte[42]);
        var part = await sut.UploadPartAsync(
            created.SessionId, created.UploadToken!, 1, 0, 41, 42, body, 42, default);

        part.Should().Be(new MultipartUploadPart(1, 42, "part-etag"));
    }

    [Fact]
    public async Task UploadPartAsync_WithWrongToken_DoesNotAcceptBytes()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);

        await using var body = new MemoryStream(new byte[42]);
        var act = () => sut.UploadPartAsync(
            created.SessionId, "wrong", 1, 0, 41, 42, body, 42, default);

        await act.Should().ThrowAsync<UploadTokenInvalidException>();
    }

    [Fact]
    public async Task UploadPartAsync_WithWrongRange_DoesNotAcceptBytes()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);

        await using var body = new MemoryStream(new byte[42]);
        var act = () => sut.UploadPartAsync(
            created.SessionId, created.UploadToken!, 1, 1, 42, 42, body, 42, default);

        await act.Should().ThrowAsync<UploadPartInvalidException>();
        _objects.Verify(x => x.UploadPartAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadPartAsync_RepeatingPartNumber_ReplacesItSafely()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.UploadPartAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), 1,
                It.IsAny<Stream>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartUploadPart(1, 42, "latest-etag"));

        await using var firstBody = new MemoryStream(new byte[42]);
        await using var secondBody = new MemoryStream(new byte[42]);
        await sut.UploadPartAsync(
            created.SessionId, created.UploadToken!, 1, 0, 41, 42, firstBody, 42, default);
        var repeated = await sut.UploadPartAsync(
            created.SessionId, created.UploadToken!, 1, 0, 41, 42, secondBody, 42, default);

        repeated.Etag.Should().Be("latest-etag");
        _objects.Verify(x => x.UploadPartAsync(
            "universal-v1", created.FileId.ToString(), It.IsAny<string>(), 1,
            It.IsAny<Stream>(), 42, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ResumeAsync_ReturnsS3PartsAndRotatesToken()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MultipartUploadPart(1, 21, "etag-1")]);

        var resumed = await sut.ResumeAsync(10, created.SessionId, default);

        resumed.UploadToken.Should().NotBeNullOrWhiteSpace().And.NotBe(created.UploadToken);
        resumed.UploadedParts.Should().ContainSingle().Which.Should().Be(new MultipartUploadPart(1, 21, "etag-1"));
    }

    [Fact]
    public async Task ResumeAsync_InvalidatesPreviousToken()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await sut.ResumeAsync(10, created.SessionId, default);
        await using var body = new MemoryStream(new byte[42]);
        var act = () => sut.UploadPartAsync(
            created.SessionId, created.UploadToken!, 1, 0, 41, 42, body, 42, default);

        await act.Should().ThrowAsync<UploadTokenInvalidException>();
    }

    [Fact]
    public async Task ResumeAsync_WhenMultipartWasAlreadyCompleted_RecoversProcessingState()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("multipart no longer exists"));
        _objects.Setup(x => x.HeadAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartObjectInfo("object-etag", 42));

        var resumed = await sut.ResumeAsync(10, created.SessionId, default);

        resumed.Status.Should().Be(UploadSessionStatus.Processing);
        resumed.UploadToken.Should().BeNull();
        _processing.Verify(x => x.PublishAsync(created.SessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_WhenAllPartsExist_MovesSessionToProcessing()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MultipartUploadPart(1, 42, "etag-1")]);
        _objects.Setup(x => x.CompleteAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<MultipartUploadPart>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompletedMultipartObject("object-etag", 42));

        var completed = await sut.CompleteAsync(10, created.SessionId, default);

        completed.Status.Should().Be(UploadSessionStatus.Processing);
        completed.UploadToken.Should().BeNull();
        _processing.Verify(x => x.PublishAsync(created.SessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_AfterAmbiguousS3Completion_UsesHeadAndPublishesOnce()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("multipart response was lost"));
        _objects.Setup(x => x.HeadAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartObjectInfo("object-etag", 42));

        var completed = await sut.CompleteAsync(10, created.SessionId, default);
        var replay = await sut.CompleteAsync(10, created.SessionId, default);

        completed.Status.Should().Be(UploadSessionStatus.Processing);
        replay.Status.Should().Be(UploadSessionStatus.Processing);
        _processing.Verify(x => x.PublishAsync(created.SessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_WhenListPartsIsEmptyButObjectExists_RecoversThroughHead()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MultipartUploadPart>());
        _objects.Setup(x => x.HeadAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartObjectInfo("object-etag", 42));

        var completed = await sut.CompleteAsync(10, created.SessionId, default);

        completed.Status.Should().Be(UploadSessionStatus.Processing);
        _processing.Verify(x => x.PublishAsync(created.SessionId, It.IsAny<CancellationToken>()), Times.Once);
        _objects.Verify(x => x.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<MultipartUploadPart>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteAsync_WhenPublishingFails_RollsBackStateAndCanRecoverCompletedObject()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MultipartUploadPart(1, 42, "etag-1")]);
        _objects.Setup(x => x.CompleteAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<MultipartUploadPart>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompletedMultipartObject("object-etag", 42));
        _processing.SetupSequence(x => x.PublishAsync(created.SessionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("outbox unavailable"))
            .Returns(Task.CompletedTask);

        var act = () => sut.CompleteAsync(10, created.SessionId, default);
        await act.Should().ThrowAsync<IOException>();

        _database.Context.ChangeTracker.Clear();
        var rolledBack = _database.Context.UploadSessions.Single(x => x.Id == created.SessionId);
        rolledBack.Status.Should().Be(UploadSessionStatus.Uploading);
        rolledBack.ReservedBytes.Should().Be(42);

        _database.Context.ChangeTracker.Clear();
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("multipart already completed"));
        _objects.Setup(x => x.HeadAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartObjectInfo("object-etag", 42));

        var recovered = await CreateSut().CompleteAsync(10, created.SessionId, default);

        recovered.Status.Should().Be(UploadSessionStatus.Processing);
    }

    [Fact]
    public async Task CompleteAsync_WhenPartsAreMissing_KeepsSessionUploading()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MultipartUploadPart>());

        var act = () => sut.CompleteAsync(10, created.SessionId, default);

        await act.Should().ThrowAsync<UploadPartsIncompleteException>();
        (await sut.GetAsync(10, created.SessionId, default)).Status.Should().Be(UploadSessionStatus.Uploading);
    }

    [Fact]
    public async Task CompleteAsync_WhenAnotherRequestAlreadyMovedSession_ReturnsCurrentState()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MultipartUploadPart(1, 42, "etag-1")]);
        _objects.Setup(x => x.CompleteAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<MultipartUploadPart>>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var competing = _database.CreateAdditionalContext();
                await competing.UploadSessions
                    .Where(x => x.Id == created.SessionId)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, UploadSessionStatus.Processing)
                        .SetProperty(x => x.CompletedEtag, "competing-etag")
                        .SetProperty(x => x.UploadTokenHash, string.Empty)
                        .SetProperty(x => x.ConcurrencyToken, Guid.NewGuid()));
                return new CompletedMultipartObject("object-etag", 42);
            });

        var result = await sut.CompleteAsync(10, created.SessionId, default);

        result.Status.Should().Be(UploadSessionStatus.Processing);
        result.UploadToken.Should().BeNull();
    }

    [Fact]
    public async Task CancelAsync_AbortsMultipartAndReleasesReservation()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);

        var cancelled = await sut.CancelAsync(10, created.SessionId, default);

        cancelled.Status.Should().Be(UploadSessionStatus.Cancelled);
        _database.Context.UploadSessions.Single(x => x.Id == created.SessionId).ReservedBytes.Should().Be(0);
        _objects.Verify(x => x.AbortAsync(
            "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAsync_WhenAbortFails_KeepsPlaceholderForCleanupRetry()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.AbortAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("ambiguous abort"));

        var cancelled = await sut.CancelAsync(10, created.SessionId, default);

        cancelled.Status.Should().Be(UploadSessionStatus.Cancelled);
        var stored = _database.Context.UploadSessions.Single(x => x.Id == created.SessionId);
        stored.CleanupPending.Should().BeTrue();
        stored.ReservedBytes.Should().Be(0);
        _database.Context.UploadedFiles.Should().ContainSingle(x => x.Id == created.FileId);
    }

    [Fact]
    public void CalculatePartSize_AlwaysKeepsPartCountWithinS3Limit()
    {
        var fileSize = BasePartSizeTimesMaximumParts() + 1;

        var partSize = UploadSessionCoordinator.CalculatePartSize(fileSize);

        ((fileSize + partSize - 1) / partSize).Should().BeLessThanOrEqualTo(UploadSessionCoordinator.MaximumParts);
    }

    [Fact]
    public void CalculatePartSize_AtMaximumObjectSize_DoesNotOverflow()
    {
        var fileSize = UploadSessionCoordinator.MaximumObjectSize;

        var partSize = UploadSessionCoordinator.CalculatePartSize(fileSize);

        var partCount = fileSize / partSize + (fileSize % partSize == 0 ? 0 : 1);
        partCount.Should().BeLessThanOrEqualTo(UploadSessionCoordinator.MaximumParts);
    }

    [Fact]
    public void CalculatePartSize_AboveMaximumObjectSize_Throws()
    {
        var act = () => UploadSessionCoordinator.CalculatePartSize(
            UploadSessionCoordinator.MaximumObjectSize + 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static long BasePartSizeTimesMaximumParts() =>
        UploadSessionCoordinator.BasePartSize * UploadSessionCoordinator.MaximumParts;

    private UploadSessionCoordinator CreateSut() => new(
        _database.Context,
        new StorageQuotaService(_database.Context, _limits.Object),
        _objects.Object,
        _processing.Object,
        _profiles,
        _time,
        NullLogger<UploadSessionCoordinator>.Instance);

    private static CreateUploadSessionInput Input(string key, long size) => new(
        key,
        "movie.mp4",
        size,
        "video/mp4",
        new string('a', 64));

    public void Dispose()
    {
        _profiles.Dispose();
        _database.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
