using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using Xunit;

using StorageCutoverStatus = BarkCloud.Proto.Files.StorageCutoverStatus;

namespace BarkCloud.Files.Tests.Services;

public sealed class VideoVariantWorkerTests
{
    [Fact]
    public async Task UploadPersistsIntentBeforePutAndCopiesCurrentOwnersOnCommit()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var originalId = Guid.NewGuid();
        await fixture.AddOriginalAsync(originalId, [42, 84]);
        var variantId = Guid.NewGuid();
        var outputPath = fixture.CreateOutputFile();
        var intentWasVisibleAtPut = false;
        fixture.Client.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutObjectRequest, CancellationToken>(async (_, _) =>
            {
                await using var context = fixture.Database.CreateAdditionalContext();
                var state = await context.VideoTranscodeStates.FindAsync(originalId);
                intentWasVisibleAtPut = state?.PendingVariantFileId == variantId
                    && state.PendingProfileId == WorkerFixture.VariantProfileId;
                return new PutObjectResponse { ETag = "variant-etag" };
            });

        await fixture.Worker.UploadAndCommitVariantAsync(
            await fixture.GetOriginalAsync(originalId), 360, 640, 360, 10,
            outputPath, variantId, WorkerFixture.VariantProfileId, fixture.Generation, CancellationToken.None);

        await using var result = fixture.Database.CreateAdditionalContext();
        var variant = await result.UploadedFiles.FindAsync(variantId);
        var link = await result.VideoVariants.SingleAsync();
        var stateAfterCommit = await result.VideoTranscodeStates.FindAsync(originalId);
        Assert.True(intentWasVisibleAtPut);
        Assert.Equal(new long[] { 42, 84 }, variant!.Uploaders);
        Assert.Equal(UploadFileType.VideoVariant, variant.Type);
        Assert.Equal(10, variant.Size);
        Assert.Equal(360, link.Height);
        Assert.Equal(640, link.Width);
        Assert.Equal(360, link.ActualHeight);
        Assert.Null(stateAfterCommit!.PendingVariantFileId);
        Assert.Null(stateAfterCommit.PendingProfileId);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task IfOriginalIsRemovedAfterPutTheObjectIsDeletedAndIntentCleared()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var originalId = Guid.NewGuid();
        await fixture.AddOriginalAsync(originalId, [42]);
        var variantId = Guid.NewGuid();
        var outputPath = fixture.CreateOutputFile();
        var intentWasVisibleAtPut = false;
        fixture.Client.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutObjectRequest, CancellationToken>(async (_, _) =>
            {
                await using (var checkContext = fixture.Database.CreateAdditionalContext())
                {
                    var state = await checkContext.VideoTranscodeStates.FindAsync(originalId);
                    intentWasVisibleAtPut = state?.PendingVariantFileId == variantId;
                }

                await using var removeContext = fixture.Database.CreateAdditionalContext();
                var original = await removeContext.UploadedFiles.FindAsync(originalId);
                removeContext.UploadedFiles.Remove(original!);
                await removeContext.SaveChangesAsync();
                return new PutObjectResponse { ETag = "variant-etag" };
            });

        var originalForCommit = await fixture.GetOriginalAsync(originalId);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Worker.UploadAndCommitVariantAsync(
            originalForCommit, 360, 640, 360, 10,
            outputPath, variantId, WorkerFixture.VariantProfileId, fixture.Generation, CancellationToken.None));

        await using var result = fixture.Database.CreateAdditionalContext();
        var stateAfterCleanup = await result.VideoTranscodeStates.FindAsync(originalId);
        Assert.True(intentWasVisibleAtPut);
        Assert.False(await result.UploadedFiles.AnyAsync(file => file.Id == variantId));
        Assert.False(await result.VideoVariants.AnyAsync());
        Assert.Null(stateAfterCleanup!.PendingVariantFileId);
        Assert.Null(stateAfterCleanup.PendingProfileId);
        fixture.Client.Verify(client => client.DeleteObjectAsync(
            It.Is<DeleteObjectRequest>(request => request.Key == variantId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task TrackedUploadActivityCoversVariantPutAndCommit()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var originalId = Guid.NewGuid();
        await fixture.AddOriginalAsync(originalId, [42]);
        var variantId = Guid.NewGuid();
        var outputPath = fixture.CreateOutputFile();
        var putEntered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPutToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutObjectRequest, CancellationToken>(async (_, _) =>
            {
                try
                {
                    await using var context = fixture.Database.CreateAdditionalContext();
                    putEntered.SetResult(await context.StorageWriteActivities.CountAsync());
                    await allowPutToFinish.Task;
                    return new PutObjectResponse { ETag = "variant-etag" };
                }
                catch (Exception exception)
                {
                    putEntered.TrySetException(exception);
                    throw;
                }
            });

        var original = await fixture.GetOriginalAsync(originalId);
        var upload = fixture.Worker.UploadAndCommitVariantAsync(
            original, 360, 640, 360, 10,
            outputPath, variantId, WorkerFixture.VariantProfileId, fixture.Generation, CancellationToken.None);
        var activeActivitiesDuringPut = await putEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        allowPutToFinish.SetResult();
        await upload;

        await using var result = fixture.Database.CreateAdditionalContext();
        Assert.Equal(1, activeActivitiesDuringPut);
        Assert.Equal(0, await result.StorageWriteActivities.CountAsync());
    }

    [Fact]
    public async Task FailedS3CompensationKeepsIntentForRecovery()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var originalId = Guid.NewGuid();
        await fixture.AddOriginalAsync(originalId, [42]);
        var variantId = Guid.NewGuid();
        var outputPath = fixture.CreateOutputFile();
        fixture.Client.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("temporary upload failure"));
        fixture.Client.Setup(client => client.DeleteObjectAsync(
                It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("temporary cleanup failure"));

        var original = await fixture.GetOriginalAsync(originalId);
        await Assert.ThrowsAsync<IOException>(() => fixture.Worker.UploadAndCommitVariantAsync(
            original, 360, 640, 360, 10,
            outputPath, variantId, WorkerFixture.VariantProfileId, fixture.Generation, CancellationToken.None));

        await using var result = fixture.Database.CreateAdditionalContext();
        var state = await result.VideoTranscodeStates.FindAsync(originalId);
        Assert.Equal(variantId, state!.PendingVariantFileId);
        Assert.Equal(WorkerFixture.VariantProfileId, state.PendingProfileId);
    }

    [Fact]
    public async Task FailedVideosStopBeingCandidatesAfterThreeAttempts()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var fileId = Guid.NewGuid();
        await fixture.AddOriginalAsync(fileId, [42]);

        for (var attempt = 0; attempt < 2; attempt++)
            await fixture.Worker.MarkFailedAsync(fileId, fixture.Generation, "encode failed", CancellationToken.None);
        Assert.NotNull(await fixture.Worker.FindCandidateAsync(CancellationToken.None));

        await fixture.Worker.MarkFailedAsync(fileId, fixture.Generation, "encode failed", CancellationToken.None);

        Assert.Null(await fixture.Worker.FindCandidateAsync(CancellationToken.None));
        await using var context = fixture.Database.CreateAdditionalContext();
        var state = await context.VideoTranscodeStates.FindAsync(fileId);
        Assert.Equal(3, state!.Attempts);
        Assert.Equal(VideoTranscodeStatus.Failed, state.Status);
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        public const string VariantProfileId = "video-variants-v1";

        private readonly ServiceProvider _services;
        private readonly S3BucketRegistry _registry;
        private readonly VideoTranscodeLeaseManager _leaseManager;
        private readonly string _outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.webm");

        public SqliteFilesContext Database { get; } = new();
        public Mock<IAmazonS3> Client { get; } = new();
        public VideoVariantWorker Worker { get; }
        public long Generation { get; }

        private WorkerFixture()
        {
            var registry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
            _registry = registry.Object;
            var profile = new StorageProfileOptions
            {
                ProfileId = VariantProfileId,
                Role = "video-variants",
                Version = 1,
                ServiceUrl = "https://s3.example",
                AccessKey = "access",
                SecretKey = "secret",
                BucketName = "video-variants",
                Region = "us-east-1",
                ForcePathStyle = true,
                IsActive = true
            };
            registry.Setup(value => value.GetProfile(VariantProfileId)).Returns(profile);
            registry.Setup(value => value.GetClientForProfile(VariantProfileId)).Returns(Client.Object);
            Client.Setup(value => value.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutObjectResponse { ETag = "variant-etag" });
            Client.Setup(value => value.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteObjectResponse());

            var services = new ServiceCollection();
            services.AddScoped<FilesContext>(_ => Database.CreateAdditionalContext());
            services.AddSingleton(_registry);
            services.AddSingleton(serviceProvider => new StorageMigrationGate(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(), _registry));
            services.AddScoped(serviceProvider => new S3Uploader(
                _registry, serviceProvider.GetRequiredService<StorageMigrationGate>()));
            _services = services.BuildServiceProvider();

            var scopeFactory = _services.GetRequiredService<IServiceScopeFactory>();
            _leaseManager = new VideoTranscodeLeaseManager(scopeFactory, TimeProvider.System);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["TRANSCODER_TOKEN"] = "test-token" })
                .Build();
            Worker = new VideoVariantWorker(
                scopeFactory,
                _leaseManager,
                Mock.Of<IHttpClientFactory>(),
                configuration,
                Options.Create(new VideoVariantOptions { Enabled = true, WorkDirectory = Path.GetTempPath() }),
                TimeProvider.System,
                NullLogger<VideoVariantWorker>.Instance,
                "files-test");
            Generation = _leaseManager.TryAcquireAsync("files-test", CancellationToken.None).GetAwaiter().GetResult()!.Value;
        }

        public static Task<WorkerFixture> CreateAsync() => Task.FromResult(new WorkerFixture());

        public async Task AddOriginalAsync(Guid id, List<long> uploaders)
        {
            await using var context = Database.CreateAdditionalContext();
            var now = DateTime.UtcNow;
            context.UploadedFiles.Add(new UploadFile
            {
                Id = id,
                Uploaders = uploaders,
                CreatedAt = now,
                UploadedAt = now,
                Etag = "original-etag",
                Type = UploadFileType.CloudFile,
                StorageProfileId = "videos-v1",
                MediaKind = MediaKind.Video,
                Filename = "source.mp4",
                Size = 100
            });
            context.VideoTranscodeStates.Add(new VideoTranscodeState { FileId = id, UpdatedAt = now });
            await context.SaveChangesAsync();
        }

        public async Task<UploadFile> GetOriginalAsync(Guid id)
        {
            await using var context = Database.CreateAdditionalContext();
            return (await context.UploadedFiles.FindAsync(id))!;
        }

        public string CreateOutputFile()
        {
            File.WriteAllBytes(_outputPath, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
            return _outputPath;
        }

        public async ValueTask DisposeAsync()
        {
            await _leaseManager.ReleaseAsync("files-test", Generation);
            _services.Dispose();
            Database.Dispose();
            if (File.Exists(_outputPath))
                File.Delete(_outputPath);
        }
    }
}
