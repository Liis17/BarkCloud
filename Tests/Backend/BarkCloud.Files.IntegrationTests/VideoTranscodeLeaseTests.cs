using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BarkCloud.Files.IntegrationTests;

public sealed class VideoTranscodeLeaseTests : IAsyncLifetime
{
    private readonly UploadTestDatabase _database = new();
    private ServiceProvider? _services;
    private VideoTranscodeLeaseManager? _leaseManager;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _services = new ServiceCollection()
            .AddScoped<FilesContext>(_ => _database.CreateContext())
            .BuildServiceProvider();
        _leaseManager = new VideoTranscodeLeaseManager(
            _services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
    }

    [Fact]
    public async Task ConcurrentWorkersHaveOneOwnerAndNewGenerationFencesOldOwner()
    {
        var leaseManager = _leaseManager!;
        var acquired = await Task.WhenAll(
            leaseManager.TryAcquireAsync("files-a", CancellationToken.None),
            leaseManager.TryAcquireAsync("files-b", CancellationToken.None));

        Assert.Single(acquired, generation => generation.HasValue);
        Assert.Equal(1L, acquired.Single(generation => generation.HasValue));

        await using (var context = _database.CreateContext())
        {
            var now = DateTime.UtcNow;
            await context.VideoTranscodeLeases
                .ExecuteUpdateAsync(update => update.SetProperty(lease => lease.ExpiresAt, now.AddMinutes(-1)));
        }

        var takeover = await leaseManager.TryAcquireAsync("files-c", CancellationToken.None);
        Assert.Equal(2L, takeover);
        Assert.False(await leaseManager.RenewAsync("files-a", 1, CancellationToken.None));
        Assert.False(await leaseManager.RenewAsync("files-b", 1, CancellationToken.None));

        using var scope = _services!.CreateScope();
        var fencedContext = scope.ServiceProvider.GetRequiredService<FilesContext>();
        await using var transaction = await fencedContext.Database.BeginTransactionAsync();
        var oldOwner = acquired[0].HasValue ? "files-a" : "files-b";
        await Assert.ThrowsAsync<VideoTranscodeLeaseLostException>(() =>
            leaseManager.EnsureCurrentAsync(fencedContext, oldOwner, 1, CancellationToken.None));
        await transaction.RollbackAsync();

        await leaseManager.ReleaseAsync(oldOwner, 1);
        Assert.Null(await leaseManager.TryAcquireAsync("files-d", CancellationToken.None));
    }

    public async Task DisposeAsync()
    {
        if (_leaseManager is not null)
        {
            await using var context = _database.CreateContext();
            var lease = await context.VideoTranscodeLeases.AsNoTracking().SingleOrDefaultAsync();
            if (lease is not null)
                await _leaseManager.ReleaseAsync(lease.OwnerInstanceId, lease.Generation);
        }

        if (_services is not null)
            await _services.DisposeAsync();
        await _database.DisposeAsync();
    }
}
