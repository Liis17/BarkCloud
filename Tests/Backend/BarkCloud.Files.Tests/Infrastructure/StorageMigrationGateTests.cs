using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using UploadSessionStatus = BarkCloud.Files.Domain.UploadSessionStatus;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Tests.Infrastructure;

public sealed class StorageMigrationGateTests
{
    [Theory]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("video.mp4", "video/mp4")]
    [InlineData("music.mp3", "audio/mpeg")]
    [InlineData("music.mp3", "application/octet-stream")]
    public async Task SeparatePreviewBucket_BlocksNewMediaAdmissionAndWaitsForItsEntireResumableSession(string fileName, string contentType)
    {
        using var fixture = new Fixture(separatePreviews: true); var gate = fixture.Gate();
        await using (await gate.EnterUploadAdmissionAsync(UploadFileType.CloudFile, "universal-v1", fileName, contentType, default)) { }
        await fixture.WithDb(db =>
        {
            var session = Session(UploadSessionStatus.Uploading); session.StorageProfileId = "universal-v1";
            session.FileName = fileName; session.ContentType = contentType;
            db.UploadSessions.Add(session); return db.SaveChangesAsync();
        });
        await gate.BeginAsync(fixture.Request, default);
        await FluentActions.Awaiting(() => gate.EnterUploadAdmissionAsync(UploadFileType.CloudFile, "universal-v1", fileName, contentType, default))
            .Should().ThrowAsync<StorageMigrationPausedException>();
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).ActiveUploads.Should().Be(1);
        await fixture.WithDb(async db => { var session = await db.UploadSessions.SingleAsync(); session.Status = UploadSessionStatus.Processing; await db.SaveChangesAsync(); });
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("draining");
        await using (await gate.EnterAsync("previews-v1", false, default)) { }
        await fixture.WithDb(async db => { var session = await db.UploadSessions.SingleAsync(); session.Status = UploadSessionStatus.Ready; await db.SaveChangesAsync(); });
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("frozen");
    }

    [Fact]
    public async Task PreviewBucketMigration_DoesNotPauseOrWaitForAvatarSessions()
    {
        using var fixture = new Fixture(separatePreviews: true); var gate = fixture.Gate();
        await gate.BeginAsync(fixture.Request, default);
        await using (await gate.EnterUploadAdmissionAsync(UploadFileType.UserAvatar, "avatars-v1", "avatar.png", "image/png", default)) { }
        await fixture.WithDb(db =>
        {
            db.UploadSessions.AddRange(
                Session(UploadSessionStatus.Uploading, UploadFileType.UserAvatar),
                Session(UploadSessionStatus.Processing, UploadFileType.UserAvatar),
                Session(UploadSessionStatus.Cancelled, UploadFileType.UserAvatar, cleanupPending: true));
            return db.SaveChangesAsync();
        });
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("frozen");
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).ActiveUploads.Should().Be(0);
    }

    [Fact]
    public async Task AvatarBucketMigration_BlocksAdmissionAndWaitsForAvatarSessions()
    {
        using var fixture = new Fixture(separatePreviews: true, migrationProfileId: "avatars-v1"); var gate = fixture.Gate();
        await fixture.WithDb(db =>
        {
            db.UploadSessions.AddRange(
                Session(UploadSessionStatus.Uploading, UploadFileType.UserAvatar),
                Session(UploadSessionStatus.Processing, UploadFileType.UserAvatar),
                Session(UploadSessionStatus.Cancelled, UploadFileType.UserAvatar, cleanupPending: true),
                Session(UploadSessionStatus.Uploading, UploadFileType.CloudFile));
            return db.SaveChangesAsync();
        });
        await gate.BeginAsync(fixture.Request, default);
        await FluentActions.Awaiting(() => gate.EnterUploadAdmissionAsync(UploadFileType.UserAvatar, "avatars-v1", "avatar.png", "image/png", default))
            .Should().ThrowAsync<StorageMigrationPausedException>();
        var status = await gate.FreezeAsync(fixture.Request.MigrationId, default);
        status.State.Should().Be("draining"); status.ActiveUploads.Should().Be(3);
    }

    [Fact]
    public async Task R2HttpAndHttps_UseOneBarrierForEveryPhysicalBucketProfile()
    {
        using var fixture = new Fixture(r2: true); var gate = fixture.Gate();
        await gate.BeginAsync(fixture.Request, default);
        await FluentActions.Awaiting(() => gate.EnterAsync("cloud-files-old-v1", true, default)).Should().ThrowAsync<StorageMigrationPausedException>();
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("frozen");
        await FluentActions.Awaiting(() => gate.EnterAsync("cloud-files-old-v1", false, default)).Should().ThrowAsync<StorageMigrationPausedException>();
    }

    [Fact]
    public async Task TrackedPipeline_WaitsBetweenItsS3WritesAndDeduplicatesTheLegacySession()
    {
        using var fixture = new Fixture(); var gate = fixture.Gate(); var fileId = Guid.NewGuid();
        var activity = await gate.TrackUploadAsync(["images-v1"], default, fileId);
        await fixture.WithDb(db => { var session = Session(UploadSessionStatus.Processing); session.FileId = fileId; db.UploadSessions.Add(session); return db.SaveChangesAsync(); });
        await gate.BeginAsync(fixture.Request, default);
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).ActiveUploads.Should().Be(1);
        await FluentActions.Awaiting(() => gate.TrackUploadAsync(["images-v1"], default)).Should().ThrowAsync<StorageMigrationPausedException>();
        await using (await gate.EnterAsync("images-v1", false, default)) { }
        await fixture.WithDb(async db => { var session = await db.UploadSessions.SingleAsync(); session.Status = UploadSessionStatus.Ready; await db.SaveChangesAsync(); });
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("draining");
        await activity.DisposeAsync();
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("frozen");
    }

    [Fact]
    public async Task Restart_RemovesAbandonedActivitiesButKeepsTheFrozenBarrier()
    {
        using var fixture = new Fixture(); var gate = fixture.Gate();
        await fixture.WithDb(db => { db.StorageWriteActivities.Add(new() { Id = Guid.NewGuid().ToString(), ProfileIdsJson = "[\"images-v1\"]" }); return db.SaveChangesAsync(); });
        await gate.BeginAsync(fixture.Request, default);
        await gate.InitializeAsync(default);
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("frozen");
        await FluentActions.Awaiting(() => gate.EnterAsync("images-v1", false, default)).Should().ThrowAsync<StorageMigrationPausedException>();
    }

    [Fact]
    public async Task Draining_BlocksNewAdmissionsButAllowsExistingMutationsAndWaitsForProcessing()
    {
        using var fixture = new Fixture(); var gate = fixture.Gate();
        await fixture.WithDb(db => { db.UploadSessions.Add(Session(UploadSessionStatus.Processing)); return db.SaveChangesAsync(); });
        await gate.BeginAsync(fixture.Request, default);
        await FluentActions.Awaiting(() => gate.EnterAsync("images-v1", true, default)).Should().ThrowAsync<StorageMigrationPausedException>();
        await using (await gate.EnterAsync("images-v1", false, default)) { }
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("draining");
        await fixture.WithDb(async db => { var session = await db.UploadSessions.SingleAsync(); session.Status = UploadSessionStatus.Ready; await db.SaveChangesAsync(); });
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).State.Should().Be("frozen");
        await FluentActions.Awaiting(() => gate.EnterAsync("images-v1", false, default)).Should().ThrowAsync<StorageMigrationPausedException>();
    }

    [Fact]
    public async Task Freeze_WaitsForAnInFlightMutationBeforeSealingTheBucket()
    {
        using var fixture = new Fixture(); var gate = fixture.Gate(); await gate.BeginAsync(fixture.Request, default);
        var mutation = await gate.EnterAsync("images-v1", false, default);
        var freeze = gate.FreezeAsync(fixture.Request.MigrationId, default);
        await Task.Delay(20); freeze.IsCompleted.Should().BeFalse();
        await mutation.DisposeAsync(); (await freeze).State.Should().Be("frozen");
    }

    [Fact]
    public async Task ApplyingBarrier_SurvivesOldRuntimeRestartAndOpensOnlyAfterAllProfilesLoadDestination()
    {
        using var fixture = new Fixture(); var old = fixture.Gate();
        await old.BeginAsync(fixture.Request, default); await old.FreezeAsync(fixture.Request.MigrationId, default);
        await old.MarkApplyingAsync(fixture.Request.MigrationId, default);
        var restartedOld = fixture.Gate(); await restartedOld.InitializeAsync(default);
        await FluentActions.Awaiting(() => restartedOld.EnterAsync("images-v1", false, default)).Should().ThrowAsync<StorageMigrationPausedException>();
        using var target = fixture.Registry(target: true);
        var restartedNew = fixture.Gate(target); await restartedNew.InitializeAsync(default);
        (await restartedNew.GetAsync(fixture.Request.MigrationId, default)).Cutovers.Single().State.Should().Be("applied");
        await using (await restartedNew.EnterAsync("images-v1", true, default)) { }
        await FluentActions.Awaiting(() => restartedOld.EnterAsync("images-v1", false, default)).Should().ThrowAsync<StorageMigrationPausedException>();
    }

    [Fact]
    public async Task Restart_WithUnexpectedCredentials_DoesNotConfirmOrReopenDestination()
    {
        using var fixture = new Fixture(); var original = fixture.Gate();
        await original.BeginAsync(fixture.Request, default); await original.FreezeAsync(fixture.Request.MigrationId, default);
        await original.MarkApplyingAsync(fixture.Request.MigrationId, default);
        using var wrongTarget = fixture.Registry(target: true, secret: "unexpected-secret");
        var restarted = fixture.Gate(wrongTarget); await restarted.InitializeAsync(default);
        (await restarted.GetAsync(fixture.Request.MigrationId, default)).Cutovers.Single().State.Should().Be("applying");
        await FluentActions.Awaiting(() => restarted.EnterAsync("images-v1", false, default)).Should().ThrowAsync<StorageMigrationPausedException>();
    }

    [Fact]
    public async Task CleanupPending_IsAnActiveUploadAndSafeCancelReleasesTheBucket()
    {
        using var fixture = new Fixture(); var gate = fixture.Gate();
        await fixture.WithDb(db => { var session = Session(UploadSessionStatus.Cancelled); session.CleanupPending = true; db.UploadSessions.Add(session); return db.SaveChangesAsync(); });
        await gate.BeginAsync(fixture.Request, default);
        (await gate.FreezeAsync(fixture.Request.MigrationId, default)).ActiveUploads.Should().Be(1);
        await gate.CancelAsync(fixture.Request.MigrationId, default);
        await using (await gate.EnterAsync("images-v1", true, default)) { }
    }

    private static UploadSession Session(UploadSessionStatus status, UploadFileType type = UploadFileType.CloudFile, bool cleanupPending = false) => new()
    {
        Id = Guid.NewGuid(), FileId = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid().ToString(),
        StorageProfileId = type == UploadFileType.UserAvatar ? "avatars-v1" : "images-v1", FileName = type == UploadFileType.UserAvatar ? "avatar.png" : "file.bin",
        ContentType = type == UploadFileType.UserAvatar ? "image/png" : "application/octet-stream", FileType = type, Status = status, CleanupPending = cleanupPending
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _database = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        private readonly ServiceProvider _services;
        private readonly S3BucketRegistry _source;
        private readonly bool _separatePreviews;
        private readonly bool _r2;
        public StorageCutoverRequest Request { get; } = new()
        {
            MigrationId = Guid.NewGuid().ToString(), SourceServiceUrl = "https://source.example", SourceBucketName = "from",
            TargetServiceUrl = "https://target.example", TargetBucketName = "to", TargetRegion = "eu-central-1", TargetForcePathStyle = false
        };
        public Fixture(bool separatePreviews = false, bool r2 = false, string? migrationProfileId = null)
        {
            _separatePreviews = separatePreviews; _r2 = r2;
            Request.TargetConnectionHash = S3Endpoint.ConnectionHash("https://target.example", "to", "access", "secret", "eu-central-1", false, false);
            _services = new ServiceCollection().AddDbContext<FilesContext>(o => o.UseSqlite("Data Source=" + _database)).BuildServiceProvider();
            using var scope = _services.CreateScope(); scope.ServiceProvider.GetRequiredService<FilesContext>().Database.EnsureCreated();
            _source = Registry(false);
            if (separatePreviews)
            {
                var profileId = migrationProfileId ?? "previews-v1";
                Request.ProfileIds.Add(profileId);
                Request.SourceBucketName = profileId == "avatars-v1" ? "avatars" : "previews";
            }
            else Request.ProfileIds.AddRange(["images-v1", "cloud-files-old-v1"]);
        }
        public S3BucketRegistry Registry(bool target, string secret = "secret")
        {
            var values = new Dictionary<string, string?>();
            foreach (var id in _separatePreviews ? new[] { "previews-v1", "avatars-v1", "universal-v1" } : new[] { "images-v1", "cloud-files-old-v1" })
            {
                values[$"StorageProfiles:{id}:ProfileId"] = id; values[$"StorageProfiles:{id}:Role"] = id.Split("-v")[0];
                values[$"StorageProfiles:{id}:Version"] = "1"; values[$"StorageProfiles:{id}:IsActive"] = (id != "cloud-files-old-v1").ToString();
                values[$"StorageProfiles:{id}:ServiceUrl"] = target ? "https://target.example" : "https://source.example";
                values[$"StorageProfiles:{id}:BucketName"] = target ? "to" : id switch { "previews-v1" => "previews", "avatars-v1" => "avatars", _ => "from" };
                if (id == "universal-v1") values[$"StorageProfiles:{id}:BucketName"] = "other";
                if (_r2 && !target) { values[$"StorageProfiles:{id}:IsR2"] = "true"; if (id == "cloud-files-old-v1") values[$"StorageProfiles:{id}:ServiceUrl"] = "http://source.example:9000"; }
                values[$"StorageProfiles:{id}:Region"] = target ? "eu-central-1" : "";
                values[$"StorageProfiles:{id}:ForcePathStyle"] = (!target).ToString();
                values[$"StorageProfiles:{id}:AccessKey"] = "access"; values[$"StorageProfiles:{id}:SecretKey"] = secret;
                values[$"StorageProfiles:{id}:IsLegacy"] = (id == "cloud-files-old-v1").ToString();
            }
            return new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        }
        public StorageMigrationGate Gate(S3BucketRegistry? registry = null) => new(_services.GetRequiredService<IServiceScopeFactory>(), registry ?? _source);
        public async Task WithDb(Func<FilesContext, Task> action)
        { using var scope = _services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<FilesContext>()); }
        public void Dispose() { _source.Dispose(); _services.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(_database); }
    }
}
