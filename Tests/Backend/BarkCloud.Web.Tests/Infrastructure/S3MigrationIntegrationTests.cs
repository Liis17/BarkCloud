using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.Proto.Configuration;
using BarkCloud.Proto.Files;
using BarkCloud.Web.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class MigrationS3FactAttribute : FactAttribute
{
    public MigrationS3FactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BARKCLOUD_MIGRATION_SOURCE_ENDPOINT"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BARKCLOUD_MIGRATION_TARGET_ENDPOINT")))
            Skip = "Требуются два выделенных тестовых S3 endpoint (BARKCLOUD_MIGRATION_*).";
    }
}

public sealed class S3MigrationIntegrationTests
{
    [MigrationS3Fact]
    public async Task TwoRealS3Endpoints_CopyAndFinalSync_VerifyKeysMetadataMultipartRangesAndUnchangedSource()
    {
        var access = Environment.GetEnvironmentVariable("BARKCLOUD_MIGRATION_ACCESS_KEY") ?? "migration-test-access";
        var secret = Environment.GetEnvironmentVariable("BARKCLOUD_MIGRATION_SECRET_KEY") ?? "migration-test-secret";
        var suffix = Guid.NewGuid().ToString("N");
        var source = new MigrationConnection(Environment.GetEnvironmentVariable("BARKCLOUD_MIGRATION_SOURCE_ENDPOINT")!, access, secret, "bc-source-" + suffix, Region: "us-east-1");
        var target = new MigrationConnection(Environment.GetEnvironmentVariable("BARKCLOUD_MIGRATION_TARGET_ENDPOINT")!, access, secret, "bc-target-" + suffix, Region: "us-east-1");
        var factory = new MigrationS3ClientFactory(); using var first = factory.Create(source); using var second = factory.Create(target);
        var uploaded = new List<string>();
        var bufferPath = Path.Combine(Path.GetTempPath(), "bc-s3-fixture-" + suffix);
        await first.PutBucketAsync(new PutBucketRequest { BucketName = source.BucketName });
        await second.PutBucketAsync(new PutBucketRequest { BucketName = target.BucketName });
        try
        {
            var fixtures = new Dictionary<string, byte[]>
            {
                ["avatars/original.jpg"] = [1, 2, 3, 4, 5, 6], ["previews/512.jpg"] = [7, 8, 9],
                ["вложенный/путь +%?#/имя.txt"] = Encoding.UTF8.GetBytes("содержимое с кириллицей"), ["empty/"] = [],
                ["encoded/content.txt.gz"] = Gzip(Encoding.UTF8.GetBytes(new string('я', 1000)))
            };
            foreach (var pair in fixtures)
            {
                using var input = new MemoryStream(pair.Value);
                var put = new PutObjectRequest { BucketName = source.BucketName, Key = pair.Key, InputStream = input, ContentType = "application/octet-stream", AutoCloseStream = false };
                put.Metadata["original-filename"] = "fixture.bin"; put.Metadata["custom"] = "metadata";
                put.Headers.CacheControl = "max-age=120"; put.Headers.ContentDisposition = "attachment; filename=fixture.bin";
                put.Headers["Content-Language"] = "ru";
                put.Headers["Expires"] = DateTime.UtcNow.AddDays(1).ToString("r", CultureInfo.InvariantCulture);
                if (pair.Key.EndsWith(".gz")) put.Headers["Content-Encoding"] = "gzip";
                await first.PutObjectAsync(put); uploaded.Add(pair.Key);
            }
            const string largeKey = "multipart/большой +файл.bin";
            await using (var file = new FileStream(bufferPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var block = RandomNumberGenerator.GetBytes(1024 * 1024);
                for (var i = 0; i < 65; i++) await file.WriteAsync(block);
                file.Position = 0;
                await first.PutObjectAsync(new PutObjectRequest { BucketName = source.BucketName, Key = largeKey, InputStream = file, AutoCloseStream = false, ContentType = "application/octet-stream" });
                uploaded.Add(largeKey);
            }
            var before = await List(first, source.BucketName);
            await second.PutBucketVersioningAsync(new PutBucketVersioningRequest
            { BucketName = target.BucketName, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
            var copier = new S3MigrationCopier(factory);
            await copier.CheckAsync(target, default);
            (await second.ListVersionsAsync(new ListVersionsRequest { BucketName = target.BucketName })).Versions.Should().BeNullOrEmpty();
            var control = new Mock<IStorageMigrationControl>(MockBehavior.Strict);
            var profile = source.ToProto(); profile.ProfileId = "universal-v1"; profile.Role = "universal"; profile.Version = 1; profile.IsActive = true;
            StorageCutoverStatus? barrier = null;
            control.Setup(x => x.GetProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([profile]);
            control.Setup(x => x.GetCutoversAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => barrier is null ? [] : [barrier]);
            control.Setup(x => x.PreflightAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            control.Setup(x => x.BeginAsync(It.IsAny<string>(), It.IsAny<MigrationSource>(), It.IsAny<MigrationConnection>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, MigrationSource selected, MigrationConnection destination, CancellationToken _) =>
                {
                    barrier = new() { MigrationId = id, State = "draining", SourceServiceUrl = selected.ServiceUrl, SourceBucketName = selected.BucketName,
                        TargetServiceUrl = destination.ServiceUrl, TargetBucketName = destination.BucketName, TargetRegion = destination.Region, TargetForcePathStyle = destination.ForcePathStyle };
                    barrier.ProfileIds.AddRange(selected.ProfileIds); return barrier;
                });
            control.Setup(x => x.FreezeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => { barrier!.State = "frozen"; return barrier; });
            control.Setup(x => x.MarkApplyingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => { barrier!.State = "applying"; return Task.CompletedTask; });
            control.Setup(x => x.RelocateAsync(It.IsAny<string>(), It.IsAny<MigrationSource>(), It.IsAny<MigrationConnection>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() => { profile.ServiceUrl = target.ServiceUrl; profile.BucketName = target.BucketName; profile.AccessKey = target.AccessKey;
                    profile.SecretKey = target.SecretKey; profile.Region = target.Region; profile.ForcePathStyle = target.ForcePathStyle; return Task.CompletedTask; });
            control.Setup(x => x.RestartAndVerifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => { barrier!.State = "applied"; return Task.CompletedTask; });
            using var worker = new StorageMigrationService(control.Object, factory, copier, NullLogger<StorageMigrationService>.Instance);
            await worker.StartAsync(default);
            try
            {
                var selected = (await worker.GetSourcesAsync()).Single();
                var check = await worker.CheckAsync(selected.Id, target, "integration", default);
                var job = worker.Start(check.ValidationId, "integration");
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                while (worker.GetJob(job.Id).State is "queued" or "running") await Task.Delay(100, deadline.Token);
                var done = worker.GetJob(job.Id);
                done.State.Should().Be("copied", done.Error);
                done.CopiedFiles.Should().Be(uploaded.Count);
                var after = await List(first, source.BucketName); after.Should().BeEquivalentTo(before);
                var copies = await List(second, target.BucketName); copies.Select(x => x.Key).Should().BeEquivalentTo(uploaded);

                // The main copy is live: final sync must transmit only a new and a changed object.
                using (var changed = new MemoryStream([1, 2, 3, 4, 5, 6, 7]))
                    await first.PutObjectAsync(new PutObjectRequest { BucketName = source.BucketName, Key = "avatars/original.jpg", InputStream = changed, ContentType = "image/jpeg", AutoCloseStream = false });
                using (var added = new MemoryStream([10, 11]))
                    await first.PutObjectAsync(new PutObjectRequest { BucketName = source.BucketName, Key = "new/created.txt", InputStream = added, ContentType = "text/plain", AutoCloseStream = false });
                uploaded.Add("new/created.txt"); var frozenSource = await List(first, source.BucketName);
                await worker.ApplyAsync(job.Id, default);
                while (worker.GetJob(job.Id).State is "queued" or "running") await Task.Delay(100, deadline.Token);
                var applied = worker.GetJob(job.Id); applied.State.Should().Be("completed", applied.Error);
                applied.CopiedFiles.Should().Be(uploaded.Count);
                (await List(first, source.BucketName)).Should().BeEquivalentTo(frozenSource);
                var versionsAfterSync = (await second.ListVersionsAsync(new ListVersionsRequest { BucketName = target.BucketName })).Versions;
                versionsAfterSync.Should().HaveCount(uploaded.Count + 1);
                versionsAfterSync.Count(x => x.Key == largeKey).Should().Be(1, "unchanged multipart must not be sent again");
                copies = await List(second, target.BucketName); copies.Select(x => x.Key).Should().BeEquivalentTo(uploaded);
                foreach (var key in uploaded)
                {
                    using var original = await first.GetObjectAsync(source.BucketName, key);
                    using var copy = await second.GetObjectAsync(target.BucketName, key);
                    (await SHA256.HashDataAsync(copy.ResponseStream)).Should().Equal(await SHA256.HashDataAsync(original.ResponseStream));
                    copy.ContentLength.Should().Be(original.ContentLength);
                    foreach (var name in original.Metadata.Keys) copy.Metadata[name].Should().Be(original.Metadata[name]);
                    copy.Headers.ContentType.Should().Be(original.Headers.ContentType);
                    copy.Headers.CacheControl.Should().Be(original.Headers.CacheControl);
                    copy.Headers.ContentDisposition.Should().Be(original.Headers.ContentDisposition);
                    foreach (var header in new[] { "Content-Encoding", "Content-Language", "Expires" })
                        copy.Headers[header].Should().Be(original.Headers[header]);
                }
                using var range = await second.GetObjectAsync(new GetObjectRequest { BucketName = target.BucketName, Key = "avatars/original.jpg", ByteRange = new ByteRange(1, 3) });
                using var ranged = new MemoryStream(); await range.ResponseStream.CopyToAsync(ranged); ranged.ToArray().Should().Equal(2, 3, 4);
                copies.Single(x => x.Key == largeKey).Etag.Should().Contain("-2");
            }
            finally { await worker.StopAsync(default); }
        }
        finally
        {
            File.Delete(bufferPath);
            // Cleanup only buckets and objects created by this test, including destination versions.
            foreach (var key in uploaded) await first.DeleteObjectAsync(source.BucketName, key);
            var versions = await second.ListVersionsAsync(new ListVersionsRequest { BucketName = target.BucketName });
            foreach (var version in versions.Versions ?? []) await second.DeleteObjectAsync(new DeleteObjectRequest
            { BucketName = target.BucketName, Key = version.Key, VersionId = version.VersionId });
            await first.DeleteBucketAsync(source.BucketName); await second.DeleteBucketAsync(target.BucketName);
        }
    }

    private static async Task<List<MigrationObject>> List(IAmazonS3 client, string bucket)
    { var list = new List<MigrationObject>(); await foreach (var item in S3MigrationCopier.ListAsync(client, bucket, default)) list.Add(item); return list; }

    private static byte[] Gzip(byte[] bytes)
    { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes); return output.ToArray(); }
}
