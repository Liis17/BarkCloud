using BarkCloud.Files.Domain;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public class UploadArtifactCleanerTests
{
    [Fact]
    public async Task CleanupAsync_ReleasesOwnerFromOriginalAndDerivedArtifacts()
    {
        using var database = new SqliteFilesContext();
        var originalId = Guid.NewGuid();
        var previewId = Guid.NewGuid();
        database.Context.UploadedFiles.AddRange(
            File(originalId, [42]),
            File(previewId, [42]));
        database.Context.FilePreviews.Add(new FilePreview
        {
            Id = Guid.NewGuid(),
            OriginalFileId = originalId,
            PreviewFileId = previewId,
            TargetWidth = 512,
            ActualWidth = 512,
            ActualHeight = 512,
            CreatedAt = DateTime.UtcNow
        });
        await database.Context.SaveChangesAsync();
        var purge = new Mock<ITrashPurgeService>();
        purge.Setup(x => x.PurgeOrphanBlobsAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(originalId)),
                It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                database.Context.UploadedFiles.Single(x => x.Id == originalId).Uploaders.Should().BeEmpty();
                database.Context.UploadedFiles.Single(x => x.Id == previewId).Uploaders.Should().BeEmpty();
            })
            .ReturnsAsync(2);
        var session = Session(originalId, ownerId: 42);

        await new UploadArtifactCleaner(
            database.Context,
            purge.Object,
            NullLogger<UploadArtifactCleaner>.Instance).CleanupAsync(session, default);

        database.Context.ChangeTracker.Clear();
        database.Context.UploadedFiles.Single(x => x.Id == previewId).Uploaders.Should().BeEmpty();
        purge.VerifyAll();
    }

    [Fact]
    public async Task CleanupAsync_KeepsSharedPreviewOwnerWhenAnotherOriginalNeedsIt()
    {
        using var database = new SqliteFilesContext();
        var originalId = Guid.NewGuid();
        var otherOriginalId = Guid.NewGuid();
        var previewId = Guid.NewGuid();
        database.Context.UploadedFiles.AddRange(
            File(originalId, [42]),
            File(otherOriginalId, [42]),
            File(previewId, [42]));
        database.Context.FilePreviews.AddRange(
            Preview(originalId, previewId, 512),
            Preview(otherOriginalId, previewId, 512));
        await database.Context.SaveChangesAsync();
        var purge = new Mock<ITrashPurgeService>();
        purge.Setup(x => x.PurgeOrphanBlobsAsync(
                It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await new UploadArtifactCleaner(
            database.Context,
            purge.Object,
            NullLogger<UploadArtifactCleaner>.Instance).CleanupAsync(Session(originalId, 42), default);

        database.Context.UploadedFiles.Single(x => x.Id == previewId).Uploaders.Should().Equal(42);
    }

    private static UploadFile File(Guid id, List<long> uploaders) => new()
    {
        Id = id,
        Uploaders = uploaders,
        CreatedAt = DateTime.UtcNow,
        UploadedAt = DateTime.UtcNow,
        Etag = "etag",
        Type = UploadFileType.CloudFile,
        StorageProfileId = "universal-v1",
        Filename = "file.bin"
    };

    private static FilePreview Preview(Guid originalId, Guid previewId, int width) => new()
    {
        Id = Guid.NewGuid(),
        OriginalFileId = originalId,
        PreviewFileId = previewId,
        TargetWidth = width,
        ActualWidth = width,
        ActualHeight = width,
        CreatedAt = DateTime.UtcNow
    };

    private static UploadSession Session(Guid fileId, long ownerId) => new()
    {
        Id = Guid.NewGuid(),
        FileId = fileId,
        OwnerId = ownerId,
        CleanupPending = true,
        ConcurrencyToken = Guid.NewGuid()
    };
}
