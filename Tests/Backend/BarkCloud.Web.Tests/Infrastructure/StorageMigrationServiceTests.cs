using System.Text.Json;

using BarkCloud.Proto.Configuration;
using BarkCloud.Proto.Files;
using BarkCloud.Web.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class StorageMigrationServiceTests
{
    [Fact]
    public async Task ExistingCopies_MetadataHeaderCasingMustNotCauseAnotherUpload()
    {
        using var fixture = new Fixture();
        fixture.Source.Put("a", [1, 2], new() { ["Original-Filename"] = "video.mp4", ["Custom"] = "Value" });
        fixture.Target.Put("a", [1, 2], new() { ["original-filename"] = "video.mp4", ["custom"] = "Value" });
        fixture.Source.Put("missing", [3]);
        await fixture.Service.StartAsync(default); var job = await fixture.Copy();
        job.CopiedBytes.Should().Be("3"); job.CopiedFiles.Should().Be(2);
        job.SkippedFiles.Should().Be(1); job.UploadedFiles.Should().Be(1);
        fixture.Target.Written.Where(x => !x.StartsWith(".barkcloud-migration-check/")).Should().Equal(new[] { "missing" },
            "HTTP metadata names are case-insensitive, so an already correct object must not be uploaded again");
    }

    [Fact]
    public async Task ExistingCopies_DefaultDestinationHeadersAcceptedByCopyMustNotCauseAnotherUpload()
    {
        using var fixture = new Fixture();
        fixture.Source.Put("a", [1, 2], headers: new());
        fixture.Target.Put("a", [1, 2], headers: new() { ["Content-Type"] = "application/octet-stream", ["Cache-Control"] = "private" });
        fixture.Source.Put("missing", [3]);
        await fixture.Service.StartAsync(default); var job = await fixture.Copy();
        job.CopiedBytes.Should().Be("3"); job.CopiedFiles.Should().Be(2);
        fixture.Target.Written.Where(x => !x.StartsWith(".barkcloud-migration-check/")).Should().Equal("missing");
    }

    [Fact]
    public async Task Sources_GroupPhysicalBucketsIncludingLegacyVersionsWithoutSecrets()
    {
        using var fixture = new Fixture();
        var sources = await fixture.Service.GetSourcesAsync();
        sources.Should().ContainSingle().Which.ProfileIds.Should().BeEquivalentTo("images-v1", "images-v2", "cloud-files-old-v1");
        var json = JsonSerializer.Serialize(sources);
        json.Should().NotContain("source-secret").And.NotContain("source-access");
    }

    [Fact]
    public async Task Sources_R2HttpAndHttpsUseTheSamePhysicalBucketAndRejectSelfCopy()
    {
        using var fixture = new Fixture();
        foreach (var profile in fixture.Profiles)
        { profile.IsR2 = true; profile.ServiceUrl = profile.IsActive ? "https://source.example" : "http://source.example:9000"; }
        var selected = (await fixture.Service.GetSourcesAsync()).Should().ContainSingle().Subject;
        selected.ProfileIds.Should().HaveCount(3);
        await FluentActions.Awaiting(() => fixture.Service.CheckAsync(selected.Id,
            new("http://source.example", "access", "secret", "from", IsR2: true), "admin", default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*самого себя*");
    }

    [Fact]
    public async Task Start_AllowsExistingObjectsAndUsesOnlyServerValidatedParameters()
    {
        using var fixture = new Fixture();
        fixture.Source.Put("a", [1, 2]); fixture.Target.Put("a", [1, 2]);
        fixture.Target.Objects["a"] = fixture.Target.Objects["a"] with { Etag = "different-multipart-etag-2" };
        fixture.Source.Put("b", [3, 4]); fixture.Target.Put("b", [8, 9]);
        fixture.Source.Put("c", [5]);
        fixture.Source.Put("d", [6], new() { ["custom"] = "source" }); fixture.Target.Put("d", [6], new() { ["custom"] = "old" });
        var reasons = new Dictionary<string, string?>();
        fixture.Target.OnPut = key =>
        {
            if (!key.StartsWith(".barkcloud-migration-check/")) reasons[key] = fixture.Service.GetJobs().Single().CurrentReason;
        };
        await fixture.Service.StartAsync(default);
        var validation = await fixture.Prepare();
        fixture.Target.Put("external", [1]);
        var job = fixture.Service.Start(validation.ValidationId, "admin");
        var done = await fixture.Wait(job.Id, "copied");
        done.CopiedBytes.Should().Be("6"); done.CopiedFiles.Should().Be(4);
        done.SkippedFiles.Should().Be(1); done.UploadedFiles.Should().Be(3);
        reasons.Should().BeEquivalentTo(new Dictionary<string, string?> { ["b"] = "content", ["c"] = "missing", ["d"] = "metadata" });
        fixture.Target.Written.Should().NotContain("a").And.Contain(["b", "c", "d"]);
        fixture.Target.Objects["b"].Data.Should().Equal(3, 4);
        fixture.Target.Objects["d"].Metadata.Should().BeEquivalentTo(fixture.Source.Objects["d"].Metadata);
        fixture.Target.Objects["external"].Data.Should().Equal(1);
        fixture.Source.Written.Should().BeEmpty(); fixture.Source.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_ExistingCopyScanKeepsVerifiedCountersAndFinishesBeforeNewUploads()
    {
        using var fixture = new Fixture();
        fixture.Source.Put("a", [1, 2]); fixture.Target.Put("a", [1, 2]);
        fixture.Source.Put("b", [3, 4, 5]); fixture.Target.Put("b", [3, 4, 5]); fixture.Source.Put("c", [6]);
        await fixture.Service.StartAsync(default); var check = await fixture.Prepare();
        fixture.Target.CorruptReadKey = "b";
        var job = fixture.Service.Start(check.ValidationId, "admin"); var failed = await fixture.Wait(job.Id, "failed");
        failed.Phase.Should().Be("checking-existing"); failed.CopiedBytes.Should().Be("2"); failed.CopiedFiles.Should().Be(1);
        failed.SkippedFiles.Should().Be(1); failed.UploadedFiles.Should().Be(0);
        fixture.Target.Written.Should().NotContain("c");
        fixture.Target.CorruptReadKey = null;
        fixture.Target.OnPut = key =>
        {
            key.Should().Be("c"); var progress = fixture.Service.GetJob(job.Id);
            progress.CopiedBytes.Should().Be("5"); progress.CopiedFiles.Should().Be(2);
        };
        fixture.Service.Retry(job.Id); var done = await fixture.Wait(job.Id, "copied");
        done.CopiedBytes.Should().Be("6"); done.CopiedFiles.Should().Be(3);
        done.SkippedFiles.Should().Be(2); done.UploadedFiles.Should().Be(1);
        fixture.Target.Written.Should().NotContain("a").And.NotContain("b").And.Contain("c");
    }

    [Fact]
    public async Task Failure_ReportsExceptionTypeAndSafeS3CodeWithoutRawMessagesOrCredentials()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", [1]);
        var logger = new Mock<ILogger<StorageMigrationService>>(); using var service = fixture.NewService(logger.Object);
        await service.StartAsync(default);
        try
        {
            var selected = (await service.GetSourcesAsync()).Single();
            var check = await service.CheckAsync(selected.Id, Fixture.TargetConnection, "admin", default);
            fixture.Target.Client.Setup(x => x.PutObjectAsync(It.Is<Amazon.S3.Model.PutObjectRequest>(r => r.Key == "a"), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Amazon.S3.AmazonS3Exception("target-secret source-access PRIVATE")
                { StatusCode = System.Net.HttpStatusCode.GatewayTimeout, ErrorCode = "RequestTimeout" });
            var job = service.Start(check.ValidationId, "admin");
            for (var i = 0; i < 500 && service.GetJob(job.Id).State != "failed"; i++) await Task.Delay(10);
            var failed = service.GetJob(job.Id); failed.State.Should().Be("failed");
            failed.Error.Should().Contain("HTTP 504").And.Contain("RequestTimeout").And.NotContain("target-secret").And.NotContain("PRIVATE");
            var log = logger.Invocations.Single(x => x.Method.Name == "Log");
            log.Arguments[2].ToString().Should().Contain("AmazonS3Exception").And.Contain("504").And.Contain("RequestTimeout").And.NotContain("PRIVATE");
            log.Arguments[3].Should().BeNull("raw SDK exceptions may contain secrets");
            StorageMigrationService.SafeError(new Amazon.S3.AmazonS3Exception("PRIVATE") { ErrorCode = "PRIVATE", StatusCode = System.Net.HttpStatusCode.BadRequest })
                .Should().Contain("HTTP 400").And.NotContain("PRIVATE");
            StorageMigrationService.SafeError(new IOException("PRIVATE")).Should().Contain("Соединение").And.NotContain("PRIVATE");
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task NewTaskAfterWebRestart_VerifiesExistingObjectsAndOnlyCopiesRemainingFiles()
    {
        using var fixture = new Fixture();
        fixture.Source.Put("a", [1, 2]); fixture.Source.Put("b", [3, 4, 5]);
        await fixture.Service.StartAsync(default);
        fixture.Target.FailPutKey = "b";
        var validation = await fixture.Prepare(); var initial = fixture.Service.Start(validation.ValidationId, "admin");
        (await fixture.Wait(initial.Id, "failed")).CopiedBytes.Should().Be("2");
        await fixture.Service.StopAsync(default);
        fixture.Target.FailPutKey = null; fixture.Target.Written.Clear();
        using var restarted = fixture.NewService(); await restarted.StartAsync(default);
        var selected = (await restarted.GetSourcesAsync()).Single();
        var checkedTarget = await restarted.CheckAsync(selected.Id, Fixture.TargetConnection, "admin", default);
        var next = restarted.Start(checkedTarget.ValidationId, "admin");
        for (var i = 0; i < 500 && restarted.GetJob(next.Id).State is not ("copied" or "failed"); i++) await Task.Delay(10);
        var done = restarted.GetJob(next.Id); done.State.Should().Be("copied");
        done.CopiedBytes.Should().Be("5"); done.CopiedFiles.Should().Be(2);
        done.SkippedFiles.Should().Be(1); done.UploadedFiles.Should().Be(1);
        fixture.Target.Written.Where(x => !x.StartsWith(".barkcloud-migration-check/")).Should().Equal("b");
        await restarted.StopAsync(default);
    }

    [Fact]
    public async Task Resume_RepeatsUnverifiedObjectWithoutDoubleCountingConfirmedBytes()
    {
        using var fixture = new Fixture();
        fixture.Source.Put("a", [1, 2]); fixture.Source.Put("b", [3, 4, 5]);
        await fixture.Service.StartAsync(default);
        var validation = await fixture.Prepare();
        fixture.Target.CorruptReadKey = "b";
        var job = fixture.Service.Start(validation.ValidationId, "admin");
        var failed = await fixture.Wait(job.Id, "failed");
        failed.CopiedBytes.Should().Be("2"); failed.CopiedFiles.Should().Be(1); failed.CurrentKey.Should().Be("b");
        failed.UploadedFiles.Should().Be(1);
        JsonSerializer.Serialize(failed).Should().NotContain("target-secret").And.NotContain("source-secret");
        fixture.Target.CorruptReadKey = null;
        fixture.Service.Retry(job.Id);
        var done = await fixture.Wait(job.Id, "copied");
        done.CopiedBytes.Should().Be("5"); done.CopiedFiles.Should().Be(2);
        done.UploadedFiles.Should().Be(2);
        fixture.Target.Written.Count(x => x == "a").Should().Be(1);
        fixture.Target.Written.Count(x => x == "b").Should().Be(2);
        fixture.Target.Objects["b"].Data.Should().Equal(3, 4, 5);
    }

    [Fact]
    public async Task Apply_FinalSyncCopiesNewAndChangedObjectsThenRelocatesAllProfileIdsAndRestarts()
    {
        using var fixture = new Fixture();
        fixture.Source.Put("a", [1]); fixture.Source.Put("b", [2]);
        await fixture.Service.StartAsync(default);
        var job = await fixture.Copy();
        fixture.Source.Put("b", [3, 4]); fixture.Source.Put("c", [5]);
        fixture.Target.Written.Clear();
        await fixture.Service.ApplyAsync(job.Id, default);
        var done = await fixture.Wait(job.Id, "completed");
        done.CopiedBytes.Should().Be("4"); done.CopiedFiles.Should().Be(3);
        done.UploadedFiles.Should().Be(4, "counts confirmed upload operations, including a changed object in final sync");
        fixture.Target.Written.Should().Equal("b", "c");
        fixture.Profiles.Select(x => x.ProfileId).Should().BeEquivalentTo("images-v1", "images-v2", "cloud-files-old-v1");
        fixture.Profiles.Should().OnlyContain(x => x.ServiceUrl == Fixture.TargetConnection.ServiceUrl && x.BucketName == "to");
        fixture.Events.Should().Equal("preflight", "preflight", "begin", "freeze", "mark", "relocate", "restart");
        fixture.Source.Written.Should().BeEmpty(); fixture.Source.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task FinalSync_DetectsMetadataOnlyChangeEvenWhenEtagSizeAndTimestampStayEqual()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", [1], new() { ["custom"] = "before" });
        await fixture.Service.StartAsync(default); var job = await fixture.Copy();
        var original = fixture.Source.Objects["a"];
        fixture.Source.Objects["a"] = original with { Metadata = new() { ["x-amz-meta-custom"] = "after" } };
        await fixture.Service.ApplyAsync(job.Id, default); await fixture.Wait(job.Id, "completed");
        fixture.Target.Objects["a"].Metadata["x-amz-meta-custom"].Should().Be("after");
        fixture.Target.Written.Count(x => x == "a").Should().Be(2);
    }

    [Fact]
    public async Task MissingDestinationAfterWrite_IsNotCountedAsADeletedSource()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", [1]);
        await fixture.Service.StartAsync(default); var validation = await fixture.Prepare();
        fixture.Target.Client.Setup(x => x.GetObjectAsync(It.Is<Amazon.S3.Model.GetObjectRequest>(r => r.Key == "a"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MigrationS3Fake.Error(System.Net.HttpStatusCode.NotFound));
        var job = fixture.Service.Start(validation.ValidationId, "admin"); var failed = await fixture.Wait(job.Id, "failed");
        failed.TotalFiles.Should().Be(1); failed.CopiedFiles.Should().Be(0); failed.CurrentKey.Should().Be("a");
    }

    [Fact]
    public async Task LostConfigurationReply_RetryDetectsCommittedConnectionsWithoutAnotherRelocation()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", [1]);
        await fixture.Service.StartAsync(default); var job = await fixture.Copy(); fixture.LoseRelocationReply = true;
        await fixture.Service.ApplyAsync(job.Id, default); var failed = await fixture.Wait(job.Id, "failed");
        fixture.Barriers.Single().State.Should().Be("applying");
        fixture.Profiles.Should().OnlyContain(x => x.ServiceUrl == "https://target.example");
        await FluentActions.Awaiting(() => fixture.Service.CancelAsync(job.Id, default)).Should().ThrowAsync<InvalidOperationException>();
        fixture.Barriers.Single().State.Should().Be("applying");
        fixture.Service.Retry(job.Id); await fixture.Wait(job.Id, "completed");
        fixture.Events.Count(x => x == "relocate").Should().Be(1); fixture.Events.Count(x => x == "restart").Should().Be(1);
    }

    [Fact]
    public async Task WebRestart_RecoversDurableCutoverWithoutRepeatingTheCopyOrChangingConfigurationAgain()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", [1]);
        await fixture.Service.StartAsync(default); var job = await fixture.Copy(); fixture.RestartFails = true;
        await fixture.Service.ApplyAsync(job.Id, default); await fixture.Wait(job.Id, "failed");
        await fixture.Service.StopAsync(default);
        using var restarted = fixture.NewService(); await restarted.StartAsync(default);
        try
        {
            var cutover = (await restarted.GetCutoversAsync(default)).Should().ContainSingle().Subject;
            cutover.CanRecover.Should().BeTrue(); cutover.CanCancel.Should().BeFalse();
            fixture.RestartFails = false; await restarted.RecoverCutoverAsync(job.Id, "admin", default);
            for (var i = 0; i < 300 && restarted.GetJob(job.Id).State != "completed"; i++) await Task.Delay(10);
            restarted.GetJob(job.Id).State.Should().Be("completed");
            fixture.Target.Written.Count(x => x == "a").Should().Be(1); fixture.Events.Count(x => x == "relocate").Should().Be(1);
        }
        finally { await restarted.StopAsync(default); }
    }

    [Fact]
    public async Task FinalVerificationFailure_DoesNotSaveConfigurationAndCanSafelyCancelBarrier()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", [1]);
        await fixture.Service.StartAsync(default); var job = await fixture.Copy();
        fixture.Source.Put("b", [2]); fixture.Target.CorruptReadKey = "b";
        await fixture.Service.ApplyAsync(job.Id, default);
        var failed = await fixture.Wait(job.Id, "failed");
        failed.Phase.Should().Be("final-verifying"); fixture.Events.Should().NotContain("relocate");
        fixture.Profiles.Should().OnlyContain(x => x.ServiceUrl == "https://source.example");
        fixture.Barriers.Should().ContainSingle().Which.State.Should().Be("frozen");
        var cancelled = await fixture.Service.CancelAsync(job.Id, default);
        cancelled.State.Should().Be("copied"); cancelled.CopiedBytes.Should().Be("1"); fixture.Barriers.Should().BeEmpty();
    }

    [Fact]
    public async Task RestartFailure_RemainsClosedAndRetryDoesNotReturnToOldConfiguration()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", [1]);
        await fixture.Service.StartAsync(default); var job = await fixture.Copy(); fixture.RestartFails = true;
        await fixture.Service.ApplyAsync(job.Id, default);
        var failed = await fixture.Wait(job.Id, "failed");
        failed.CanCancel.Should().BeFalse(); fixture.Barriers.Single().State.Should().Be("applying");
        await FluentActions.Awaiting(() => fixture.Service.CancelAsync(job.Id, default)).Should().ThrowAsync<InvalidOperationException>();
        fixture.RestartFails = false; fixture.Service.Retry(job.Id); await fixture.Wait(job.Id, "completed");
        fixture.Events.Count(x => x == "relocate").Should().Be(1); fixture.Events.Count(x => x == "restart").Should().Be(2);
    }

    [Fact]
    public async Task PreflightFailure_DoesNotAcquireBarrierOrSaveConfiguration()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", []);
        await fixture.Service.StartAsync(default); var job = await fixture.Copy(); fixture.PreflightFails = true;
        await FluentActions.Awaiting(() => fixture.Service.ApplyAsync(job.Id, default)).Should().ThrowAsync<InvalidOperationException>();
        fixture.Barriers.Should().BeEmpty(); fixture.Events.Should().NotContain("relocate");
    }

    [Fact]
    public async Task Apply_WaitsForActiveUploadsAndSupportsCancellationWhileDraining()
    {
        using var fixture = new Fixture(); fixture.Source.Put("a", []);
        await fixture.Service.StartAsync(default); var job = await fixture.Copy(); fixture.ActiveUploads = 1;
        await fixture.Service.ApplyAsync(job.Id, default);
        for (var i = 0; i < 100 && fixture.Service.GetJob(job.Id).ActiveUploads == 0; i++) await Task.Delay(10);
        fixture.Service.GetJob(job.Id).ActiveUploads.Should().Be(1); fixture.Events.Should().NotContain("relocate");
        await fixture.Service.CancelAsync(job.Id, default); await fixture.Wait(job.Id, "copied");
        fixture.Barriers.Should().BeEmpty(); fixture.Profiles.Should().OnlyContain(x => x.ServiceUrl == "https://source.example");
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly MigrationConnection TargetConnection = new("https://target.example", "target-access", "target-secret", "to", false, "eu-central-1", false);
        public MigrationS3Fake Source { get; } = new(true);
        public MigrationS3Fake Target { get; } = new();
        public List<StorageProfileItem> Profiles { get; } = [
            Profile("images-v1", "images", 1), Profile("images-v2", "images", 2, true), Profile("cloud-files-old-v1", "cloud-files-old", 1, false, true)];
        public List<StorageCutoverStatus> Barriers { get; } = [];
        public List<string> Events { get; } = [];
        public StorageMigrationService Service { get; }
        public bool RestartFails { get; set; }
        public bool PreflightFails { get; set; }
        public bool LoseRelocationReply { get; set; }
        public int ActiveUploads { get; set; }
        private readonly Mock<IStorageMigrationControl> _control;
        public Fixture()
        {
            var control = new Mock<IStorageMigrationControl>(MockBehavior.Strict);
            _control = control;
            control.Setup(x => x.GetProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Profiles.ToArray());
            control.Setup(x => x.GetCutoversAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Barriers.ToArray());
            control.Setup(x => x.PreflightAsync(It.IsAny<CancellationToken>())).Returns(() =>
            { Events.Add("preflight"); if (PreflightFails) throw new InvalidOperationException("Restart unavailable"); return Task.CompletedTask; });
            control.Setup(x => x.BeginAsync(It.IsAny<string>(), It.IsAny<MigrationSource>(), It.IsAny<MigrationConnection>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, MigrationSource source, MigrationConnection target, CancellationToken _) =>
                {
                    Events.Add("begin");
                    var barrier = Barriers.SingleOrDefault(x => x.MigrationId == id);
                    if (barrier is not null) return barrier;
                    barrier = new() { MigrationId = id, State = "draining", SourceServiceUrl = source.ServiceUrl, SourceBucketName = source.BucketName,
                        TargetServiceUrl = target.ServiceUrl, TargetBucketName = target.BucketName, TargetRegion = target.Region,
                        TargetForcePathStyle = target.ForcePathStyle, TargetIsR2 = target.IsR2 };
                    barrier.ProfileIds.AddRange(source.ProfileIds); Barriers.Add(barrier); return barrier;
                });
            control.Setup(x => x.FreezeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string id, CancellationToken _) =>
            { Events.Add("freeze"); var b = Barriers.Single(x => x.MigrationId == id); b.ActiveUploads = ActiveUploads; if (ActiveUploads == 0) b.State = "frozen"; return b; });
            control.Setup(x => x.MarkApplyingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string id, CancellationToken _) =>
            { Events.Add("mark"); Barriers.Single(x => x.MigrationId == id).State = "applying"; return Task.CompletedTask; });
            control.Setup(x => x.CancelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string id, CancellationToken _) =>
            { Events.Add("cancel"); Barriers.RemoveAll(x => x.MigrationId == id); return Task.CompletedTask; });
            control.Setup(x => x.RelocateAsync(It.IsAny<string>(), It.IsAny<MigrationSource>(), It.IsAny<MigrationConnection>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, MigrationSource source, MigrationConnection target, string _, CancellationToken _) =>
                { Events.Add("relocate"); foreach (var p in Profiles.Where(x => source.ProfileIds.Contains(x.ProfileId)))
                    { p.ServiceUrl = target.ServiceUrl; p.BucketName = target.BucketName; p.Region = target.Region; p.ForcePathStyle = target.ForcePathStyle;
                        p.AccessKey = target.AccessKey; p.SecretKey = target.SecretKey; p.IsR2 = target.IsR2; }
                    if (LoseRelocationReply) throw new IOException("lost reply after commit"); return Task.CompletedTask; });
            control.Setup(x => x.RestartAndVerifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string id, CancellationToken _) =>
            { Events.Add("restart"); if (RestartFails) throw new InvalidOperationException("Restart failed"); Barriers.Single(x => x.MigrationId == id).State = "applied"; return Task.CompletedTask; });
            Service = NewService();
        }
        public StorageMigrationService NewService(ILogger<StorageMigrationService>? logger = null)
        { var clients = new MigrationFakeClients(Source, Target); return new(_control.Object, clients, new S3MigrationCopier(clients), logger ?? NullLogger<StorageMigrationService>.Instance); }
        public async Task<MigrationCheckResult> Prepare()
        {
            var source = (await Service.GetSourcesAsync()).Single();
            return await Service.CheckAsync(source.Id, TargetConnection, "admin", default);
        }
        public async Task<MigrationJobSnapshot> Copy()
        {
            var check = await Prepare(); var job = Service.Start(check.ValidationId, "admin"); return await Wait(job.Id, "copied");
        }
        public async Task<MigrationJobSnapshot> Wait(string id, string state)
        {
            for (var i = 0; i < 500; i++)
            {
                var job = Service.GetJob(id);
                if (job.State == state) { await Task.Delay(20); return job; }
                await Task.Delay(10);
            }
            throw new TimeoutException(JsonSerializer.Serialize(Service.GetJob(id)));
        }
        private static StorageProfileItem Profile(string id, string role, int version, bool active = false, bool legacy = false) => new()
        { ProfileId = id, Role = role, Version = version, ServiceUrl = "https://source.example", BucketName = "from",
            AccessKey = "source-access", SecretKey = "source-secret", IsActive = active, IsLegacy = legacy, QuotaBytes = 123 };
        public void Dispose() { Service.StopAsync(default).GetAwaiter().GetResult(); Service.Dispose(); }
    }
}
