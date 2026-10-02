using System.Security.Cryptography;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

/// <summary>
/// Привязка оригинала к уже существующему превью-блобу (дедуп по SHA256): живой блоб получает владельцев
/// оригинала, осиротевший — не воскрешается, вместо него заливается новый (F15).
/// </summary>
public sealed class PreviewPersistenceServiceTests : IDisposable
{
    private const string Profile = "previews-v1";
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly SqliteFilesContext _db = new();
    private readonly Mock<S3Uploader> _s3;

    public PreviewPersistenceServiceTests()
    {
        var registry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
        _s3 = new Mock<S3Uploader>(registry.Object) { CallBase = false };
        _s3.Setup(u => u.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync("etag");
    }

    [Fact]
    public async Task PersistPreviews_ExistingLiveBlob_AddsOriginalOwnersAndLinksWithoutUpload()
    {
        var preview = await SeedBlob(1);
        await SeedHash(preview);
        var original = await SeedBlob(2);

        await CreateSut().PersistPreviewsAsync(original, [new MultiPreviewItem(128, 100, 80, Bytes)], Profile, default);

        var link = await _db.Context.FilePreviews.AsNoTracking().SingleAsync(x => x.OriginalFileId == original.Id);
        link.PreviewFileId.Should().Be(preview.Id);
        (await StoredUploaders(preview.Id)).Should().BeEquivalentTo([1L, 2L]);
        _s3.Verify(u => u.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PersistPreviews_ExistingBlobWithoutOwners_UploadsNewBlobInsteadOfResurrectingIt()
    {
        var orphan = await SeedBlob();
        await SeedHash(orphan);
        var original = await SeedBlob(2);

        await CreateSut().PersistPreviewsAsync(original, [new MultiPreviewItem(128, 100, 80, Bytes)], Profile, default);

        var link = await _db.Context.FilePreviews.AsNoTracking().SingleAsync(x => x.OriginalFileId == original.Id);
        link.PreviewFileId.Should().NotBe(orphan.Id);
        (await StoredUploaders(link.PreviewFileId)).Should().Equal(2);
        (await StoredUploaders(orphan.Id)).Should().BeEmpty();
        _s3.Verify(u => u.UploadAsync(Profile, link.PreviewFileId.ToString(), It.IsAny<Stream>(), "image/jpeg"), Times.Once);
    }

    [Fact]
    public async Task PersistJpegView_ExistingLiveBlob_ReturnsItAndAddsOwners()
    {
        var view = await SeedBlob(1);
        await SeedHash(view);
        var original = await SeedBlob(2);

        var id = await CreateSut().PersistJpegViewAsync(original, Bytes, 100, 80, Profile, default);

        id.Should().Be(view.Id);
        (await StoredUploaders(view.Id)).Should().BeEquivalentTo([1L, 2L]);
        (await _db.Context.FilePreviews.AsNoTracking().SingleAsync(x => x.OriginalFileId == original.Id))
            .TargetWidth.Should().Be(0);
    }

    [Fact]
    public async Task PersistJpegView_ExistingBlobWithoutOwners_UploadsNewBlob()
    {
        var orphan = await SeedBlob();
        await SeedHash(orphan);
        var original = await SeedBlob(2);

        var id = await CreateSut().PersistJpegViewAsync(original, Bytes, 100, 80, Profile, default);

        id.Should().NotBe(orphan.Id);
        (await StoredUploaders(id)).Should().Equal(2);
    }

    private PreviewPersistenceService CreateSut() => new(
        new UploadedFilesStorage(_db.Context),
        new FileHashesStorage(_db.Context),
        _s3.Object,
        _db.Context,
        NullLogger<PreviewPersistenceService>.Instance);

    private async Task<UploadFile> SeedBlob(params long[] uploaders)
    {
        var file = new UploadFile
        {
            Id = Guid.NewGuid(),
            Uploaders = uploaders.ToList(),
            Type = UploadFileType.CloudFile,
            MediaKind = MediaKind.Photo,
            StorageProfileId = Profile,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
            Etag = "etag",
        };
        _db.Context.UploadedFiles.Add(file);
        await _db.Context.SaveChangesAsync();
        return file;
    }

    private async Task SeedHash(UploadFile file)
    {
        _db.Context.FileHashes.Add(new FileHash
        {
            FileId = file.Id,
            Hash = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(),
        });
        await _db.Context.SaveChangesAsync();
    }

    private async Task<List<long>> StoredUploaders(Guid fileId)
    {
        _db.Context.ChangeTracker.Clear();
        return (await _db.Context.UploadedFiles.SingleAsync(x => x.Id == fileId)).Uploaders;
    }

    public void Dispose() => _db.Dispose();
}
