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
