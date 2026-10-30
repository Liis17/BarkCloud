using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace BarkCloud.Files.Tests.Infrastructure;

public sealed class VideoTranscodeLeaseManagerTests : IDisposable
{
    private readonly SqliteFilesContext _database = new();
    private readonly MutableTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly ServiceProvider _services;
    private readonly VideoTranscodeLeaseManager _manager;

    public VideoTranscodeLeaseManagerTests()
    {
        _services = new ServiceCollection()
            .AddScoped<FilesContext>(_ => _database.CreateAdditionalContext())
            .BuildServiceProvider();
        _manager = new VideoTranscodeLeaseManager(_services.GetRequiredService<IServiceScopeFactory>(), _timeProvider);
    }

    [Fact]
    public async Task TryAcquireAllowsOnlyOneUnexpiredOwner()
    {
        var first = await _manager.TryAcquireAsync("files-1", CancellationToken.None);
        var second = await _manager.TryAcquireAsync("files-2", CancellationToken.None);

        Assert.Equal(1L, first);
        Assert.Null(second);
    }

    [Fact]
    public async Task NewGenerationFencesOldOwnerAndCannotBeClearedByOldRelease()
    {
        var oldGeneration = await _manager.TryAcquireAsync("files-old", CancellationToken.None);
        _timeProvider.Advance(VideoTranscodeLeaseLeaseDuration());
        var newGeneration = await _manager.TryAcquireAsync("files-new", CancellationToken.None);

        Assert.Equal(1L, oldGeneration);
        Assert.Equal(2L, newGeneration);
        Assert.False(await _manager.RenewAsync("files-old", oldGeneration!.Value, CancellationToken.None));

        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<VideoTranscodeLeaseLostException>(() =>
            _manager.EnsureCurrentAsync(context, "files-old", oldGeneration.Value, CancellationToken.None));
        await transaction.RollbackAsync();

        await _manager.ReleaseAsync("files-old", oldGeneration.Value);
        Assert.Null(await _manager.TryAcquireAsync("files-third", CancellationToken.None));
    }

    private static TimeSpan VideoTranscodeLeaseLeaseDuration() => TimeSpan.FromMinutes(2);

    public void Dispose()
    {
        _services.Dispose();
        _database.Dispose();
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
