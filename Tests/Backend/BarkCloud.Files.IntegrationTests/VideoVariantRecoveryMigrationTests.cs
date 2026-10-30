using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Configurations;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Shared.Exceptions.Files;
using BarkCloud.GrpcServer;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using StorageCutoverRequest = BarkCloud.Proto.Files.StorageCutoverRequest;

namespace BarkCloud.Files.IntegrationTests;

public sealed class VideoVariantRecoveryMigrationTests
{
    private const string ProfileId = "universal-v1";

    [Fact]
    public async Task DrainingBarrierWaitsForCrashedIntentAndRecoveryDeletesFromSourceBeforeCutover()
    {
        await using var database = new UploadTestDatabase();
        await database.InitializeAsync();
        using var fixture = new Fixture(database);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var originalId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        await fixture.AddPendingVariantAsync(originalId, variantId, includeUploader: false);

        var gate = fixture.Gate();
        await using var activeActivity = await gate.TrackUploadAsync([ProfileId], ct, variantId);
        await gate.BeginAsync(fixture.Request, ct);
        var whileActive = await gate.FreezeAsync(fixture.Request.MigrationId, ct);
        Assert.Equal("draining", whileActive.State);
        Assert.Equal(1, whileActive.ActiveUploads);

        await activeActivity.DisposeAsync();
        await fixture.AddAbandonedActivityAsync(variantId);
        await gate.InitializeAsync(ct);
        var afterRestart = await gate.FreezeAsync(fixture.Request.MigrationId, ct);
        Assert.Equal("draining", afterRestart.State);
        Assert.Equal(1, afterRestart.ActiveUploads);

        await using (var purgeContext = database.CreateContext())
        {
            var purge = fixture.CreatePurge(purgeContext);
            Assert.Equal(0, await purge.PurgeOrphanBlobsAsync([originalId], ct));
        }
        await using (var context = database.CreateContext())
        {
            Assert.True(await context.UploadedFiles.AnyAsync(file => file.Id == originalId, ct));
            Assert.Equal(variantId, (await context.VideoTranscodeStates.SingleAsync(state => state.FileId == originalId, ct)).PendingVariantFileId);
        }
        fixture.Client.Verify(client => client.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        await fixture.Worker.ReconcilePendingIntentsAsync(fixture.Generation, ct);

        var afterRecovery = await gate.FreezeAsync(fixture.Request.MigrationId, ct);
        Assert.Equal("frozen", afterRecovery.State);
        Assert.Equal(0, afterRecovery.ActiveUploads);
        await using (var context = database.CreateContext())
            Assert.Null((await context.VideoTranscodeStates.SingleAsync(state => state.FileId == originalId, ct)).PendingVariantFileId);
        fixture.Client.Verify(client => client.DeleteObjectAsync(
            It.Is<DeleteObjectRequest>(request => request.BucketName == "from" && request.Key == variantId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);

        await gate.MarkApplyingAsync(fixture.Request.MigrationId, ct);
        await Assert.ThrowsAsync<StorageMigrationPausedException>(
            () => gate.EnterAsync(ProfileId, false, ct));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly UploadTestDatabase _database;
        private readonly ServiceProvider _services;
        private readonly Mock<S3BucketRegistry> _registry;
        private readonly StorageMigrationGate _gate;
        private readonly VideoTranscodeLeaseManager _leaseManager;
        private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public Mock<IAmazonS3> Client { get; } = new();
        public StorageCutoverRequest Request { get; } = new()
        {
            MigrationId = Guid.NewGuid().ToString(),
            SourceServiceUrl = "https://source.example",
            SourceBucketName = "from",
            TargetServiceUrl = "https://target.example",
            TargetBucketName = "to",
            TargetRegion = "eu-central-1",
            TargetForcePathStyle = true
        };
        public long Generation { get; }
        public VideoVariantWorker Worker { get; }

        public Fixture(UploadTestDatabase database)
        {
            _database = database;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"StorageProfiles:{ProfileId}:ProfileId"] = ProfileId,
                [$"StorageProfiles:{ProfileId}:Role"] = "universal",
                [$"StorageProfiles:{ProfileId}:Version"] = "1",
                [$"StorageProfiles:{ProfileId}:IsActive"] = "true",
                [$"StorageProfiles:{ProfileId}:ServiceUrl"] = "https://source.example",
                [$"StorageProfiles:{ProfileId}:BucketName"] = "from",
                [$"StorageProfiles:{ProfileId}:Region"] = "",
                [$"StorageProfiles:{ProfileId}:ForcePathStyle"] = "true",
                [$"StorageProfiles:{ProfileId}:AccessKey"] = "test",
                [$"StorageProfiles:{ProfileId}:SecretKey"] = "test"
            }).Build();
            _registry = new Mock<S3BucketRegistry>(config) { CallBase = true };
            _registry.Setup(registry => registry.GetClientForProfile(ProfileId)).Returns(Client.Object);
            Client.Setup(client => client.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteObjectResponse());
            Request.ProfileIds.Add(ProfileId);
            Request.TargetConnectionHash = S3Endpoint.ConnectionHash(
                "https://target.example", "to", "test", "test", "eu-central-1", true, false);

            var services = new ServiceCollection();
            services.AddScoped<FilesContext>(_ => _database.CreateContext());
            services.AddSingleton(_registry.Object);
            services.AddSingleton(serviceProvider => new StorageMigrationGate(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(), _registry.Object));
            services.AddScoped(serviceProvider => new S3Uploader(
                _registry.Object, serviceProvider.GetRequiredService<StorageMigrationGate>()));
            _services = services.BuildServiceProvider();

            var scopeFactory = _services.GetRequiredService<IServiceScopeFactory>();
            _gate = _services.GetRequiredService<StorageMigrationGate>();
            _leaseManager = new VideoTranscodeLeaseManager(scopeFactory, TimeProvider.System);
            Generation = _leaseManager.TryAcquireAsync("files-recovery", CancellationToken.None).GetAwaiter().GetResult()!.Value;
            var workerConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TRANSCODER_TOKEN"] = "test-token"
            }).Build();
            Worker = new VideoVariantWorker(
                scopeFactory,
                _leaseManager,
                Mock.Of<IHttpClientFactory>(),
                workerConfiguration,
                Options.Create(new VideoVariantOptions { Enabled = true, WorkDirectory = _workDirectory }),
                TimeProvider.System,
                NullLogger<VideoVariantWorker>.Instance,
                "files-recovery");
        }

        public StorageMigrationGate Gate() => _gate;

        public async Task AddPendingVariantAsync(Guid originalId, Guid variantId, bool includeUploader)
        {
            await using var context = _database.CreateContext();
            var now = DateTime.UtcNow;
            context.UploadedFiles.Add(new UploadFile
            {
                Id = originalId,
                Uploaders = includeUploader ? [42] : [],
                CreatedAt = now,
                UploadedAt = now,
                Etag = "original-etag",
                Type = UploadFileType.CloudFile,
                StorageProfileId = ProfileId,
                MediaKind = MediaKind.Video,
                Filename = "source.mp4",
                Size = 100
            });
            context.VideoTranscodeStates.Add(new VideoTranscodeState
            {
                FileId = originalId,
                PendingVariantFileId = variantId,
                PendingProfileId = ProfileId,
                UpdatedAt = now
            });
            await context.SaveChangesAsync();
        }

        public async Task AddAbandonedActivityAsync(Guid variantId)
        {
            await using var context = _database.CreateContext();
            context.StorageWriteActivities.Add(new StorageWriteActivity
            {
                Id = Guid.NewGuid().ToString("N"),
                FileId = variantId,
                ProfileIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { ProfileId })
            });
            await context.SaveChangesAsync();
        }

        public TrashPurgeService CreatePurge(FilesContext context)
        {
            var uploader = new S3Uploader(_registry.Object, _gate);
            return new TrashPurgeService(
                context, uploader, _registry.Object, new FileHashesStorage(context),
                NullLogger<TrashPurgeService>.Instance);
        }

        public void Dispose()
        {
            _leaseManager.ReleaseAsync("files-recovery", Generation).GetAwaiter().GetResult();
            _services.Dispose();
            _registry.Object.Dispose();
            if (Directory.Exists(_workDirectory))
                Directory.Delete(_workDirectory, recursive: true);
        }
    }
}
