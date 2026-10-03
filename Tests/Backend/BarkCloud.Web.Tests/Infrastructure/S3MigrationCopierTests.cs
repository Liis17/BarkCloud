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
    public async Task Check_RejectsNonEmptyDestinationWithoutDeletingAnything()
    {
        var source = new MigrationS3Fake(true); var target = new MigrationS3Fake(); target.Put("existing", [1]);
        var action = () => new S3MigrationCopier(new MigrationFakeClients(source, target)).CheckAsync(Target, default);
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*пустым*");
        target.Deleted.Should().BeEmpty(); target.Written.Should().BeEmpty();
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
