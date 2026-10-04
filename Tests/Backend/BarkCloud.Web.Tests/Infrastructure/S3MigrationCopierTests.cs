using System.Net;
using System.Text;
using System.Text.Json;

using Amazon.S3.Model;

using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class S3MigrationCopierTests
{
    private static readonly MigrationConnection Source = new("https://source.example", "source-access", "source-secret", "from");
    private static readonly MigrationConnection Target = new("https://target.example", "target-access", "target-secret", "to");

    [Theory]
    [InlineData("size")]
    [InlineData("metadata")]
    [InlineData("header:Content-Type")]
    [InlineData("content")]
    public async Task ExistingCopy_StillRejectsActualDifferencesAndReportsReasonWithoutMetadataValues(string reason)
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake();
        source.Put("same-id", [1, 2], new() { ["Custom"] = "Value" });
        target.Put("same-id", reason == "size" ? [1] : reason == "content" ? [3, 4] : [1, 2],
            new() { ["custom"] = reason == "metadata" ? "value" : "Value" },
            new() { ["Content-Type"] = reason == "header:Content-Type" ? "video/mp4" : "application/octet-stream" });
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var check = await new S3MigrationCopier(new MigrationFakeClients(source, target)).VerifyExistingAsync(source.Client.Object, Source,
                target.Client.Object, Target, new("same-id", 2, "", null), directory, _ => {}, default);
            check.Verified.Should().BeNull(); check.CopyReason.Should().Be(reason);
            target.Written.Should().BeEmpty(); source.Written.Should().BeEmpty();
            target.Client.Verify(x => x.GetObjectMetadataAsync("to", "same-id", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Copy_RetriesS3RequestTimeoutOrHttpClientTimeoutWithoutRestartingTheObject(bool s3Timeout)
    {
        var source = new MigrationS3Fake(true)
        {
            FailReadOnce = s3Timeout ? new Amazon.S3.AmazonS3Exception("untrusted timeout message")
                { StatusCode = HttpStatusCode.BadRequest, ErrorCode = "RequestTimeout" } : new TaskCanceledException("HTTP timeout")
        };
        var target = new MigrationS3Fake(); source.Put("key", [1, 2, 3]);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await new S3MigrationCopier(new MigrationFakeClients(source, target)).CopyAsync(source.Client.Object, Source,
                target.Client.Object, Target, new("key", 3, "", null), directory, _ => {}, default);
            source.Client.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
            target.Written.Should().Equal("key"); target.Objects["key"].Data.Should().Equal(1, 2, 3);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Multipart_BrokenRangeRetriesOnlyThatRangeWithoutDuplicatingPartsOrHashInput()
    {
        var source = new MigrationS3Fake(true) { DisconnectReadOffsetOnce = S3MigrationCopier.PartSize };
        var target = new MigrationS3Fake(); source.Put("large", new byte[S3MigrationCopier.PartSize + 128 * 1024]);
        var item = source.Objects["large"]; var uploads = 0;
        target.OnUploadPart = () => { uploads++; source.OpenReads.Should().Be(0); };
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var result = await new S3MigrationCopier(new MigrationFakeClients(source, target)).CopyAsync(source.Client.Object, Source,
                target.Client.Object, Target, new("large", item.Data.LongLength, item.Etag, item.Modified), directory, _ => {}, default);
            source.Reads.Select(x => x.ByteRange.Start).Should().Equal(0, S3MigrationCopier.PartSize, S3MigrationCopier.PartSize);
            uploads.Should().Be(2); result.Sha256.Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(item.Data)));
            target.Objects["large"].Data.Should().Equal(item.Data);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Multipart_SourceChangeBetweenRangesAbortsUploadAndClearsTemporaryBuffers()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake();
        source.Put("large", new byte[S3MigrationCopier.PartSize + 3]); var item = source.Objects["large"];
        target.OnUploadPart = () => source.Objects["large"] = item with { Etag = "changed" };
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await FluentActions.Awaiting(() => new S3MigrationCopier(new MigrationFakeClients(source, target)).CopyAsync(source.Client.Object, Source,
                target.Client.Object, Target, new("large", item.Data.LongLength, item.Etag, item.Modified), directory, _ => {}, default))
                .Should().ThrowAsync<Amazon.S3.AmazonS3Exception>().Where(e => e.StatusCode == HttpStatusCode.PreconditionFailed);
            target.Client.Verify(x => x.AbortMultipartUploadAsync(It.IsAny<AbortMultipartUploadRequest>(), It.IsAny<CancellationToken>()), Times.Once);
            target.Written.Should().BeEmpty(); Directory.GetFiles(directory).Should().BeEmpty();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Multipart_ReleasesSourceConnectionBeforeSlowPartUploadAndUsesBoundedConditionalRanges()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake();
        source.Put("large", new byte[S3MigrationCopier.PartSize + 3]); var item = source.Objects["large"];
        target.OnUploadPart = () => source.OpenReads.Should().Be(0, "source GET must not stay idle while a slow destination receives the part");
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await new S3MigrationCopier(new MigrationFakeClients(source, target)).CopyAsync(source.Client.Object, Source,
                target.Client.Object, Target, new("large", item.Data.LongLength, item.Etag, item.Modified), directory, _ => {}, default);
            source.Reads.Should().HaveCount(2);
            source.Reads[0].ByteRange.Start.Should().Be(0); source.Reads[0].ByteRange.End.Should().Be(S3MigrationCopier.PartSize - 1);
            source.Reads[1].ByteRange.Start.Should().Be(S3MigrationCopier.PartSize);
            source.Reads.Should().OnlyContain(x => x.EtagToMatch == item.Etag);
            target.Objects["large"].Data.Should().Equal(item.Data);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("nested/кириллица +%?#/file.txt", "данные")]
    [InlineData("empty/", "")]
    public async Task Copy_PreservesExactKeysMetadataAndHeadersWithoutWritingToSource(string key, string text)
    {
        var source = new MigrationS3Fake(readOnly: true); var target = new MigrationS3Fake();
        source.Put(key, Encoding.UTF8.GetBytes(text), new() { ["original-filename"] = "файл.txt", ["custom"] = "value" },
            new() { ["Content-Type"] = "text/plain", ["Content-Disposition"] = "attachment; filename=file.txt", ["Cache-Control"] = "max-age=120" });
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var item = source.Objects[key];
            var copier = new S3MigrationCopier(new MigrationFakeClients(source, target));
            var result = await copier.CopyAsync(source.Client.Object, Source, target.Client.Object, Target,
                new(key, item.Data.Length, item.Etag, item.Modified), directory, _ => {}, default);
            result.Sha256.Should().NotBeNullOrEmpty();
            target.Objects[key].Data.Should().Equal(item.Data);
            target.Objects[key].Metadata.Should().BeEquivalentTo(item.Metadata);
            target.Objects[key].Headers.Should().BeEquivalentTo(item.Headers);
            source.Objects.Should().ContainSingle(); source.Written.Should().BeEmpty(); source.Deleted.Should().BeEmpty();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Copy_RejectsDifferentContentOnReadBack()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake { CorruptRead = true };
        source.Put("key", [1, 2, 3]);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var action = () => new S3MigrationCopier(new MigrationFakeClients(source, target)).CopyAsync(
                source.Client.Object, Source, target.Client.Object, Target, new("key", 3, "", null), directory, _ => {}, default);
            await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*сумма*");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Copy_ResumesWhenPendingMultipartWasAlreadyCompletedOrAborted()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake(); source.Put("key", [1, 2, 3]);
        target.Client.Setup(x => x.AbortMultipartUploadAsync(It.IsAny<AbortMultipartUploadRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.S3.AmazonS3Exception("already gone") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchUpload" });
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, "multipart.ndjson");
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new[] { "key", "completed-upload" }));
        try
        {
            await new S3MigrationCopier(new MigrationFakeClients(source, target)).CopyAsync(source.Client.Object, Source,
                target.Client.Object, Target, new("key", 3, "", null), directory, _ => {}, default);
            File.Exists(pending).Should().BeFalse(); target.Objects["key"].Data.Should().Equal(1, 2, 3);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Copy_KeepsInventoryTimestampWhenHttpLastModifiedHasLowerPrecision()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake(); source.Put("key", [1]);
        var item = source.Objects["key"];
        var inventoryTime = item.Modified.AddMilliseconds(123);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var copied = await new S3MigrationCopier(new MigrationFakeClients(source, target)).CopyAsync(source.Client.Object, Source,
                target.Client.Object, Target, new("key", 1, item.Etag, inventoryTime), directory, _ => {}, default);
            copied.LastModified.Should().Be(inventoryTime);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Check_RemovesTheSpecificTestVersionAndLeavesEmptyBucket()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake { Versioned = true };
        await new S3MigrationCopier(new MigrationFakeClients(source, target)).CheckAsync(Target, default);
        target.Objects.Should().BeEmpty();
        target.Deleted.Should().ContainSingle().Which.VersionId.Should().NotBeNullOrEmpty();
        target.Written.Should().ContainSingle().Which.Should().StartWith(".barkcloud-migration-check/");
    }

    [Fact]
    public async Task Check_AllowsPartialDestinationAndOnlyDeletesItsTestObject()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake(); target.Put("existing", [1]);
        await new S3MigrationCopier(new MigrationFakeClients(source, target)).CheckAsync(Target, default);
        target.Objects.Should().ContainSingle().Which.Key.Should().Be("existing");
        target.Deleted.Should().ContainSingle().Which.Key.Should().StartWith(".barkcloud-migration-check/");
    }

    [Fact]
    public async Task Check_RejectsFailedCleanup()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake { FailCleanup = true };
        var action = () => new S3MigrationCopier(new MigrationFakeClients(source, target)).CheckAsync(Target, default);
        await action.Should().ThrowAsync<Amazon.S3.AmazonS3Exception>();
        target.Objects.Should().ContainSingle();
    }

    [Fact]
    public async Task Check_RejectsUnreadableDestinationAndStillRemovesTheTestObject()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake();
        target.Client.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MigrationS3Fake.Error(HttpStatusCode.Forbidden));
        await FluentActions.Awaiting(() => new S3MigrationCopier(new MigrationFakeClients(source, target)).CheckAsync(Target, default))
            .Should().ThrowAsync<Amazon.S3.AmazonS3Exception>();
        target.Objects.Should().BeEmpty(); target.Deleted.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Check_RejectsInvalidCredentialsOrMissingWritePermission(bool listDenied)
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake();
        if (listDenied) target.Client.Setup(x => x.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MigrationS3Fake.Error(HttpStatusCode.Forbidden));
        else target.Client.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MigrationS3Fake.Error(HttpStatusCode.Forbidden));
        await FluentActions.Awaiting(() => new S3MigrationCopier(new MigrationFakeClients(source, target)).CheckAsync(Target, default))
            .Should().ThrowAsync<Amazon.S3.AmazonS3Exception>();
        target.Objects.Should().BeEmpty(); target.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Inventory_EnumeratesEveryPageAndUnfilteredKey()
    {
        var source = new MigrationS3Fake(true) { PageSize = 1 }; source.Put("a", []); source.Put("folder/b", [2]);
        var keys = new List<string>();
        await foreach (var item in S3MigrationCopier.ListAsync(source.Client.Object, "from", default)) keys.Add(item.Key);
        keys.Should().Equal("a", "folder/b");
        source.Client.Verify(x => x.ListObjectsV2Async(It.Is<ListObjectsV2Request>(r => r.Prefix == null && r.Delimiter == null), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void Errors_DoNotExposeSdkMessagesOrCredentials() =>
        StorageMigrationService.SafeError(MigrationS3Fake.Error(HttpStatusCode.Forbidden)).Should().NotContain("untrusted");
}
