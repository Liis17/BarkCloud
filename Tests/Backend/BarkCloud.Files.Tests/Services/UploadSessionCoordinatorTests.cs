using BarkCloud.Files.Domain;
using BarkCloud.Files.Exceptions;
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
            ("StorageProfiles:universal:IsActive", "true"),
            ("StorageProfiles:avatars:ProfileId", "avatars-v1"),
            ("StorageProfiles:avatars:Role", "avatars"),
            ("StorageProfiles:avatars:Version", "1"),
            ("StorageProfiles:avatars:ServiceUrl", "http://localhost:9000"),
            ("StorageProfiles:avatars:AccessKey", "test"),
            ("StorageProfiles:avatars:SecretKey", "test-secret"),
            ("StorageProfiles:avatars:BucketName", "avatars"),
            ("StorageProfiles:avatars:IsActive", "true")));
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

    [Theory]
    [InlineData(UploadFileType.UserAvatar, "avatar.png", "image/png", "avatars-v1")]
    [InlineData(UploadFileType.CloudFile, "movie.mp4", "video/mp4", "universal-v1")]
    public async Task CreateAsync_PersistsRequestedTypeForSessionAndPlaceholderAndSelectsProfile(
        UploadFileType type, string fileName, string contentType, string profileId)
    {
        var input = Input("typed-upload", 42, type, fileName, contentType);

        var result = await CreateSut().CreateAsync(10, "Safari", input, default);

        var session = await _database.Context.UploadSessions.SingleAsync(x => x.Id == result.SessionId);
        var file = await _database.Context.UploadedFiles.SingleAsync(x => x.Id == result.FileId);
        session.FileType.Should().Be(type);
        session.StorageProfileId.Should().Be(profileId);
        file.Type.Should().Be(type);
        file.StorageProfileId.Should().Be(profileId);
        _objects.Verify(x => x.InitiateAsync(
            profileId, result.FileId.ToString(), contentType, fileName, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_AtAvatarSizeLimit_IsAccepted()
    {
        var result = await CreateSut().CreateAsync(
            10,
            "Safari",
            Input("avatar-limit", UploadSessionCoordinator.MaximumAvatarSize, UploadFileType.UserAvatar, "avatar.png", "image/png"),
            default);

        (await _database.Context.UploadSessions.SingleAsync(x => x.Id == result.SessionId)).DeclaredSize
            .Should().Be(20L * 1024 * 1024);
        UploadSessionCoordinator.MaximumAvatarSize.Should().Be(20L * 1024 * 1024);
    }

    [Fact]
    public async Task CreateAsync_AboveAvatarSizeLimit_RejectsBeforeQuotaAndMultipartReservation()
    {
        var act = () => CreateSut().CreateAsync(
            10,
            "Safari",
            Input("avatar-too-large", UploadSessionCoordinator.MaximumAvatarSize + 1, UploadFileType.UserAvatar, "avatar.png", "image/png"),
            default);

        await act.Should().ThrowAsync<InvalidUploadSessionRequestException>();
        _limits.Verify(x => x.GetLimitBytesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _objects.Verify(x => x.InitiateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _database.Context.UploadSessions.Should().BeEmpty();
        _database.Context.UploadedFiles.Should().BeEmpty();
    }

    [Theory]
    [InlineData(UploadFileType.UserAvatar, "cancel")]
    [InlineData(UploadFileType.CloudFile, "cancel")]
    [InlineData(UploadFileType.UserAvatar, "expire")]
    [InlineData(UploadFileType.CloudFile, "expire")]
    [InlineData(UploadFileType.UserAvatar, "failed-cleanup")]
    [InlineData(UploadFileType.CloudFile, "failed-cleanup")]
    public async Task CreateAsync_IdempotencyUsesPersistedTypeAfterCleanup(
        UploadFileType type, string terminalPath)
    {
        var sut = CreateSut();
        var input = Input("retained-upload", 42, type, "avatar.png", "image/png");
        var created = await sut.CreateAsync(10, "Safari", input, default);

        switch (terminalPath)
        {
            case "cancel":
                await sut.CancelAsync(10, created.SessionId, default);
                break;
            case "expire":
                var expiring = await _database.Context.UploadSessions.SingleAsync(x => x.Id == created.SessionId);
                expiring.ExpiresAt = _time.GetUtcNow().UtcDateTime.AddMinutes(-1);
                await _database.Context.SaveChangesAsync();
                await new UploadSessionMaintenance(
                    _database.Context,
                    _objects.Object,
                    Mock.Of<IUploadArtifactCleaner>(),
                    _time,
                    NullLogger<UploadSessionMaintenance>.Instance).RunOnceAsync(default);
                break;
            case "failed-cleanup":
                var failed = await _database.Context.UploadSessions.SingleAsync(x => x.Id == created.SessionId);
                failed.Status = UploadSessionStatus.Processing;
                failed.CompletedEtag = "completed-etag";
                await _database.Context.SaveChangesAsync();
                var pipeline = new Mock<IUploadEnrichmentPipeline>();
                pipeline.Setup(x => x.ProcessAsync(It.IsAny<UploadSession>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new FileIntegrityException("avatar integrity failure"));
                var cleaner = new Mock<IUploadArtifactCleaner>();
                cleaner.Setup(x => x.CleanupAsync(It.IsAny<UploadSession>(), It.IsAny<CancellationToken>()))
                    .Returns(async (UploadSession session, CancellationToken ct) =>
                    {
                        var placeholder = await _database.Context.UploadedFiles
                            .SingleAsync(x => x.Id == session.FileId, ct);
                        _database.Context.UploadedFiles.Remove(placeholder);
                        session.CleanupPending = false;
                        await _database.Context.SaveChangesAsync(ct);
                    });
                var processor = new UploadSessionProcessor(
                    _database.Context, pipeline.Object, cleaner.Object, _time,
                    NullLogger<UploadSessionProcessor>.Instance);
                (await processor.ProcessAsync(created.SessionId, default)).Should().Be(UploadProcessingOutcome.Failed);
                break;
        }

        _database.Context.UploadedFiles.Should().BeEmpty();
        var repeated = await sut.CreateAsync(10, "Safari", input, default);
        repeated.SessionId.Should().Be(created.SessionId);
        repeated.FileId.Should().Be(created.FileId);

        var otherType = type == UploadFileType.UserAvatar ? UploadFileType.CloudFile : UploadFileType.UserAvatar;
        var conflicting = () => sut.CreateAsync(
            10, "Safari", Input("retained-upload", 42, otherType, "avatar.png", "image/png"), default);
        await conflicting.Should().ThrowAsync<UploadIdempotencyConflictException>();
        _objects.Verify(x => x.InitiateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
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
        _database.Context.UploadSessionParts.Should().ContainSingle(x =>
            x.SessionId == created.SessionId
            && x.PartNumber == 1
            && x.Size == 42
            && x.Etag == "part-etag");
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
    public async Task ResumeAsync_WhenProviderOmitsEtag_UsesRecordedUploadPart()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.UploadPartAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), 1,
                It.IsAny<Stream>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartUploadPart(1, 42, "upload-etag"));
        await using var body = new MemoryStream(new byte[42]);
        await sut.UploadPartAsync(created.SessionId, created.UploadToken!, 1, 0, 41, 42, body, 42, default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MultipartUploadPart(1, 42, string.Empty)]);

        var resumed = await sut.ResumeAsync(10, created.SessionId, default);

        resumed.UploadedParts.Should().ContainSingle().Which.Should().Be(new MultipartUploadPart(1, 42, "upload-etag"));
    }

    [Fact]
    public async Task CompleteAsync_WhenProviderReturnsEmptyList_UsesRecordedUploadParts()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.UploadPartAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), 1,
                It.IsAny<Stream>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartUploadPart(1, 42, "upload-etag"));
        await using var body = new MemoryStream(new byte[42]);
        await sut.UploadPartAsync(created.SessionId, created.UploadToken!, 1, 0, 41, 42, body, 42, default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _objects.Setup(x => x.CompleteAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(),
                It.Is<IReadOnlyList<MultipartUploadPart>>(parts =>
                    parts.Count == 1
                    && parts[0] == new MultipartUploadPart(1, 42, "upload-etag")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompletedMultipartObject("object-etag", 42));

        var completed = await sut.CompleteAsync(10, created.SessionId, default);

        completed.Status.Should().Be(UploadSessionStatus.Processing);
    }

    [Fact]
    public async Task CompleteAsync_WhenProviderOmitsEtag_UsesRecordedUploadPart()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(10, "Safari", Input("upload-1", size: 42), default);
        _objects.Setup(x => x.UploadPartAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), 1,
                It.IsAny<Stream>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultipartUploadPart(1, 42, "upload-etag"));
        await using var body = new MemoryStream(new byte[42]);
        await sut.UploadPartAsync(created.SessionId, created.UploadToken!, 1, 0, 41, 42, body, 42, default);
        _objects.Setup(x => x.ListPartsAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MultipartUploadPart(1, 42, string.Empty)]);
        _objects.Setup(x => x.CompleteAsync(
                "universal-v1", created.FileId.ToString(), It.IsAny<string>(),
                It.Is<IReadOnlyList<MultipartUploadPart>>(parts =>
                    parts.Count == 1
                    && parts[0] == new MultipartUploadPart(1, 42, "upload-etag")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompletedMultipartObject("object-etag", 42));

        var completed = await sut.CompleteAsync(10, created.SessionId, default);

        completed.Status.Should().Be(UploadSessionStatus.Processing);
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

    private static CreateUploadSessionInput Input(
        string key,
        long size,
        UploadFileType fileType = UploadFileType.CloudFile,
        string fileName = "movie.mp4",
        string contentType = "video/mp4") => new(
        key,
        fileName,
        size,
        contentType,
        new string('a', 64),
        fileType);

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
