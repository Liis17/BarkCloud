using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Infrastructure;

public sealed class StorageMigrationGatePostgresTests
{
    [PostgresFact]
    public async Task OtherReplica_PreservesLivePipelineAndFreezesOnlyAfterItFinishes()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        using var fixture = new Fixture(database); var first = fixture.Gate(); var second = fixture.Gate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = deadline.Token;
        var activity = await first.TrackUploadAsync(["universal-v1"], ct);
        await second.InitializeAsync(ct);
        await second.BeginAsync(fixture.Request, ct);
        (await second.FreezeAsync(fixture.Request.MigrationId, ct)).ActiveUploads.Should().Be(1);
        await using (await first.EnterAsync("universal-v1", false, ct)) { }
        await FluentActions.Awaiting(() => first.EnterAsync("universal-v1", true, ct)).Should().ThrowAsync<StorageMigrationPausedException>();
        await activity.DisposeAsync();
        (await second.FreezeAsync(fixture.Request.MigrationId, ct)).State.Should().Be("frozen");
        await FluentActions.Awaiting(() => first.EnterAsync("universal-v1", false, ct)).Should().ThrowAsync<StorageMigrationPausedException>();
        var restarted = fixture.Gate(); await restarted.InitializeAsync(ct);
        await FluentActions.Awaiting(() => restarted.EnterAsync("universal-v1", false, ct)).Should().ThrowAsync<StorageMigrationPausedException>();
    }

    [PostgresFact]
    public async Task Begin_WaitsForMutationLeaseOnAnotherReplica()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync(); using var fixture = new Fixture(database);
        var first = fixture.Gate(); var second = fixture.Gate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = deadline.Token;
        var mutation = await first.EnterAsync("universal-v1", false, ct);
        var begin = second.BeginAsync(fixture.Request, ct);
        await Task.Delay(50, ct); begin.IsCompleted.Should().BeFalse();
        await mutation.DisposeAsync(); (await begin).State.Should().Be("draining");
    }

    [PostgresFact]
    public async Task FailedSessionSave_AbortsUnderExistingAdmissionWithoutDeadlockingQueuedCutover()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync(); using var fixture = new Fixture(database);
        var gate = fixture.Gate(); var other = fixture.Gate(); var fail = new FailSave();
        await using var db = database.CreateContext(fail);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = deadline.Token;
        Task<StorageCutoverStatus>? begin = null;
        fixture.Client.Setup(x => x.InitiateMultipartUploadAsync(It.IsAny<InitiateMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { begin = other.BeginAsync(fixture.Request, ct); await Task.Delay(50, ct); fail.Enabled = true; return new InitiateMultipartUploadResponse { UploadId = "unregistered" }; });
        fixture.Client.Setup(x => x.AbortMultipartUploadAsync(It.IsAny<AbortMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { begin!.IsCompleted.Should().BeFalse(); return new AbortMultipartUploadResponse(); });
        var quota = new Mock<IStorageQuotaService>();
        quota.Setup(x => x.GetSnapshotAsync(It.IsAny<long>(), true, It.IsAny<CancellationToken>())).ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        var coordinator = new UploadSessionCoordinator(db, quota.Object, new S3MultipartUploadStore(fixture.Registry.Object, gate),
            Mock.Of<IUploadProcessingPublisher>(), fixture.Registry.Object, TimeProvider.System, NullLogger<UploadSessionCoordinator>.Instance, gate);
        await FluentActions.Awaiting(() => coordinator.CreateAsync(1, "test", new("once", "file.bin", 3, "application/octet-stream", new string('a', 64)), ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("save failed");
        (await begin!).State.Should().Be("draining");
        fixture.Client.Verify(x => x.AbortMultipartUploadAsync(It.Is<AbortMultipartUploadRequest>(r => r.UploadId == "unregistered"), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Enabled) throw new InvalidOperationException("save failed"); return ValueTask.FromResult(result); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        public Mock<IAmazonS3> Client { get; } = new();
        public Mock<S3BucketRegistry> Registry { get; }
        public StorageCutoverRequest Request { get; } = new()
        { MigrationId = Guid.NewGuid().ToString(), SourceServiceUrl = "https://source.example", SourceBucketName = "from",
            TargetServiceUrl = "https://target.example", TargetBucketName = "to", TargetForcePathStyle = true };
        public Fixture(PostgresFilesDatabase database)
        {
            _services = new ServiceCollection().AddDbContext<FilesContext>(o => o.UseNpgsql(database.DataSource)).BuildServiceProvider();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageProfiles:universal:ProfileId"] = "universal-v1", ["StorageProfiles:universal:Role"] = "universal",
                ["StorageProfiles:universal:Version"] = "1", ["StorageProfiles:universal:IsActive"] = "true",
                ["StorageProfiles:universal:ServiceUrl"] = "https://source.example", ["StorageProfiles:universal:BucketName"] = "from",
                ["StorageProfiles:universal:AccessKey"] = "test", ["StorageProfiles:universal:SecretKey"] = "test"
            }).Build();
            Registry = new(config) { CallBase = true }; Registry.Setup(x => x.GetClientForProfile("universal-v1")).Returns(Client.Object);
            Request.ProfileIds.Add("universal-v1");
            Request.TargetConnectionHash = S3Endpoint.ConnectionHash("https://target.example", "to", "test", "test", "", true, false);
        }
        public StorageMigrationGate Gate() => new(_services.GetRequiredService<IServiceScopeFactory>(), Registry.Object);
        public void Dispose() { Registry.Object.Dispose(); _services.Dispose(); }
    }
}
