using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Infrastructure;

internal sealed class VideoTranscodeLeaseManager(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<long?> TryAcquireAsync(string ownerInstanceId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var id = VideoTranscodeLease.SingletonId;
        var emptyOwner = string.Empty;
        var initialGeneration = 0L;
        var expiredAt = now - LeaseDuration;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "VideoTranscodeLeases" ("Id", "OwnerInstanceId", "Generation", "ExpiresAt")
             VALUES ({id}, {emptyOwner}, {initialGeneration}, {expiredAt})
             ON CONFLICT ("Id") DO NOTHING
             """,
            cancellationToken);

        var lease = await LockLeaseAsync(context, cancellationToken);
        if (lease.ExpiresAt > now)
            return null;

        lease.OwnerInstanceId = ownerInstanceId;
        lease.Generation++;
        lease.ExpiresAt = now + LeaseDuration;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lease.Generation;
    }

    public async Task<bool> RenewAsync(string ownerInstanceId, long generation, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await context.VideoTranscodeLeases
            .Where(lease => lease.Id == VideoTranscodeLease.SingletonId
                && lease.OwnerInstanceId == ownerInstanceId
                && lease.Generation == generation
                && lease.ExpiresAt > now)
            .ExecuteUpdateAsync(update => update.SetProperty(lease => lease.ExpiresAt, now + LeaseDuration), cancellationToken);
        return rows == 1;
    }

    public async Task ReleaseAsync(string ownerInstanceId, long generation)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FilesContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await context.VideoTranscodeLeases
            .Where(lease => lease.Id == VideoTranscodeLease.SingletonId
                && lease.OwnerInstanceId == ownerInstanceId
                && lease.Generation == generation)
            .ExecuteUpdateAsync(update => update
                .SetProperty(lease => lease.OwnerInstanceId, string.Empty)
                .SetProperty(lease => lease.ExpiresAt, now));
    }

    public async Task EnsureCurrentAsync(
        FilesContext context,
        string ownerInstanceId,
        long generation,
        CancellationToken cancellationToken)
    {
        var lease = await LockLeaseAsync(context, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (lease.OwnerInstanceId != ownerInstanceId || lease.Generation != generation || lease.ExpiresAt <= now)
            throw new VideoTranscodeLeaseLostException();
    }

    private static Task<VideoTranscodeLease> LockLeaseAsync(FilesContext context, CancellationToken cancellationToken) =>
        context.Database.IsNpgsql()
            ? context.VideoTranscodeLeases
                .FromSqlRaw("SELECT * FROM \"VideoTranscodeLeases\" WHERE \"Id\" = 1 FOR UPDATE")
                .SingleAsync(cancellationToken)
            : context.VideoTranscodeLeases.SingleAsync(lease => lease.Id == VideoTranscodeLease.SingletonId, cancellationToken);
}

internal sealed class VideoTranscodeLeaseLostException : InvalidOperationException
{
    public VideoTranscodeLeaseLostException() : base("Экземпляр Files потерял аренду обработки видео.")
    {
    }
}
