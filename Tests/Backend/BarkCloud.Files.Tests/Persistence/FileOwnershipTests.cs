using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class FileOwnershipTests : IDisposable
{
    private readonly SqliteFilesContext _db = new();

    [Fact]
    public async Task RemoveUploaderFromAll_RemovesOnlyThatUserAndReturnsAffectedCount()
    {
        var first = await Seed(1, 2);
        var second = await Seed(1);
        var untouched = await Seed(2);

        var affected = await _db.Context.RemoveUploaderFromAllAsync(1);

        affected.Should().Be(2);
        (await StoredUploaders(first)).Should().Equal(2);
        (await StoredUploaders(second)).Should().BeEmpty();
        (await StoredUploaders(untouched)).Should().Equal(2);
    }

    [Fact]
    public async Task LockLiveFiles_ReturnsOnlyExistingFilesWithOwners()
    {
        var live = await Seed(1);
        var orphan = await Seed();

        var locked = await _db.Context.LockLiveFilesAsync([live, orphan, Guid.NewGuid()]);

        locked.Should().Equal(live);
    }

    [Fact]
    public async Task AddUploader_AddsOwnerToVideoVariantsOfOriginal()
    {
        const long owner = 7;
        var originalId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        _db.Context.UploadedFiles.AddRange(
            new UploadFile { Id = originalId, Type = UploadFileType.CloudFile, Uploaders = [1], StorageProfileId = "test" },
            new UploadFile { Id = variantId, Type = UploadFileType.VideoVariant, Uploaders = [1], StorageProfileId = "test" });
        _db.Context.VideoVariants.Add(new VideoVariant
        {
            Id = Guid.NewGuid(), OriginalFileId = originalId, VariantFileId = variantId,
            Height = 360, Width = 640, ActualHeight = 360, CreatedAt = DateTime.UtcNow
        });
        await _db.Context.SaveChangesAsync();

        await _db.Context.AddUploaderAsync(originalId, owner);

        _db.Context.ChangeTracker.Clear();
        (await _db.Context.UploadedFiles.SingleAsync(x => x.Id == originalId)).Uploaders.Should().Contain(owner);
        (await _db.Context.UploadedFiles.SingleAsync(x => x.Id == variantId)).Uploaders.Should().Contain(owner);
    }

    [Fact]
    public async Task ReleaseDerivedBlobOwner_KeepsSharedVariantUntilLastOwnedOriginalIsReleased()
    {
        const long owner = 7;
        var original1 = Guid.NewGuid();
        var original2 = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        _db.Context.UploadedFiles.AddRange(
            new UploadFile { Id = original1, Type = UploadFileType.CloudFile, Uploaders = [owner], StorageProfileId = "test" },
            new UploadFile { Id = original2, Type = UploadFileType.CloudFile, Uploaders = [owner], StorageProfileId = "test" },
            new UploadFile { Id = variantId, Type = UploadFileType.VideoVariant, Uploaders = [owner], StorageProfileId = "test" });
        _db.Context.VideoVariants.AddRange(
            new VideoVariant { Id = Guid.NewGuid(), OriginalFileId = original1, VariantFileId = variantId, Height = 360, Width = 640, ActualHeight = 360, CreatedAt = DateTime.UtcNow },
            new VideoVariant { Id = Guid.NewGuid(), OriginalFileId = original2, VariantFileId = variantId, Height = 360, Width = 640, ActualHeight = 360, CreatedAt = DateTime.UtcNow });
        await _db.Context.SaveChangesAsync();

        await _db.Context.RemoveUploaderAsync(original1, owner);
        await _db.Context.ReleaseDerivedBlobOwnerAsync([original1], owner);
        _db.Context.ChangeTracker.Clear();
        (await _db.Context.UploadedFiles.SingleAsync(x => x.Id == variantId)).Uploaders.Should().Contain(owner);

        await _db.Context.RemoveUploaderAsync(original2, owner);
        await _db.Context.ReleaseDerivedBlobOwnerAsync([original2], owner);
        _db.Context.ChangeTracker.Clear();
        (await _db.Context.UploadedFiles.SingleAsync(x => x.Id == variantId)).Uploaders.Should().NotContain(owner);
    }

    private async Task<Guid> Seed(params long[] uploaders)
    {
        var file = new UploadFile
        {
            Id = Guid.NewGuid(),
            Uploaders = uploaders.ToList(),
            StorageProfileId = "test-profile",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Context.UploadedFiles.Add(file);
        await _db.Context.SaveChangesAsync();
        return file.Id;
    }

    private async Task<List<long>> StoredUploaders(Guid fileId)
    {
        _db.Context.ChangeTracker.Clear();
        return (await _db.Context.UploadedFiles.SingleAsync(x => x.Id == fileId)).Uploaders;
    }

    public void Dispose() => _db.Dispose();
}
