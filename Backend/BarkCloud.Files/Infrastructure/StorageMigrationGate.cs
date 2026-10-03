using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Grpc.Core;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using UploadSessionStatus = BarkCloud.Files.Domain.UploadSessionStatus;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;
using MediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Infrastructure;

/// <summary>
/// Shared PostgreSQL advisory leases cover S3 mutations across Files replicas. The exclusive
/// cutover lease waits for those mutations and checks resumable sessions before sealing the bucket.
/// </summary>
public sealed class StorageMigrationGate(IServiceScopeFactory scopes, S3BucketRegistry profiles)
{
    private readonly SemaphoreSlim _localLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, byte> LocalActivities = new();

    public string ResolveLegacyProfile(UploadFile file, string fileName) => profiles.ResolveWriteProfileId(
        file.Type, BarkCloud.Files.Extensions.FileExtensions.GetMediaKind(fileName), false);

    public string[] LegacyUploadProfiles(UploadFileType type) => Enum.GetValues<MediaKind>()
        .SelectMany(kind => new[] { profiles.ResolveWriteProfileId(type, kind, false), profiles.ResolveWriteProfileId(type, kind, true) })
        .Distinct(StringComparer.Ordinal).ToArray();

    private string[] UploadProfiles(string primary, string fileName, string contentType)
    {
        var mime = string.IsNullOrWhiteSpace(contentType) || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? fileName.GetContentType() : contentType;
        return mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? [primary, profiles.ResolveWriteProfileId(UploadFileType.CloudFile, fileName.GetMediaKind(), true)] : [primary];
    }

    public async Task<IAsyncDisposable> EnterUploadAdmissionAsync(string primary, string fileName, string contentType, CancellationToken ct)
    {
        var groups = UploadProfiles(primary, fileName, contentType).Select(profiles.GetProfile)
            .GroupBy(x => NormalizeEndpoint(x.ServiceUrl, x.IsR2) + "\n" + x.BucketName).OrderBy(x => x.Key);
        var leases = new List<IAsyncDisposable>();
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<FilesContext>();
        try
        {
            foreach (var group in groups)
            {
                if (db.Database.IsNpgsql() || leases.Count == 0) leases.Add(await EnterAsync(group.First().ProfileId, true, ct));
                else await EnsureAllowedAsync(db, group.First().ProfileId, true, ct);
            }
            return new AdmissionLease(leases);
        }
        catch { foreach (var lease in leases.AsEnumerable().Reverse()) await lease.DisposeAsync(); throw; }
    }

    public async Task<UploadActivity> TrackUploadAsync(IEnumerable<string> profileIds, CancellationToken ct, Guid? fileId = null)
    {
        var ids = profileIds.Distinct(StringComparer.Ordinal).ToArray();
        var leases = new List<IAsyncDisposable>();
        var activityId = Guid.NewGuid().ToString();
        MutationLease? lifetime = null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesContext>();
        try
        {
            // Admissions and Begin use the same physical-bucket locks. The activity becomes
            // visible before admission leases are released, so Freeze cannot miss an upload.
            var groups = ids.Select(profiles.GetProfile).GroupBy(x => NormalizeEndpoint(x.ServiceUrl, x.IsR2) + "\n" + x.BucketName).OrderBy(x => x.Key).ToArray();
            foreach (var group in groups)
            {
                if (db.Database.IsNpgsql() || leases.Count == 0) leases.Add(await EnterAsync(group.First().ProfileId, true, ct));
                else await EnsureAllowedAsync(db, group.First().ProfileId, true, ct);
            }
            if (db.Database.IsNpgsql()) lifetime = await OpenActivityLockAsync(activityId, ct);
            else LocalActivities[activityId] = 0;
            db.StorageWriteActivities.Add(new() { Id = activityId, FileId = fileId, ProfileIdsJson = JsonSerializer.Serialize(ids) });
            await db.SaveChangesAsync(ct);
            return new UploadActivity(this, activityId, lifetime);
        }
        catch
        {
            LocalActivities.TryRemove(activityId, out _);
            if (lifetime is not null) await lifetime.DisposeAsync();
            throw;
        }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) await lease.DisposeAsync(); }
    }

    public async Task<bool> IsTrackedAsync(string? id, string profileId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return false;
        using var scope = scopes.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<FilesContext>().StorageWriteActivities.FindAsync([id], ct);
        return row is not null && JsonSerializer.Deserialize<string[]>(row.ProfileIdsJson)!.Contains(profileId, StringComparer.Ordinal);
    }

    public async Task<IAsyncDisposable> EnterAsync(string profileId, bool admission, CancellationToken ct)
    {
        var profile = profiles.GetProfile(profileId);
        var lease = await OpenLeaseAsync(NormalizeEndpoint(profile.ServiceUrl, profile.IsR2), profile.BucketName, exclusive: false, ct);
        try
        {
            await EnsureAllowedAsync(lease.Context, profileId, admission, ct);
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    private async Task EnsureAllowedAsync(FilesContext db, string profileId, bool admission, CancellationToken ct)
    {
        var profile = profiles.GetProfile(profileId);
        var url = NormalizeEndpoint(profile.ServiceUrl, profile.IsR2);
        var profileToken = JsonSerializer.Serialize(profileId);
        var barrier = await db.StorageCutovers.AsNoTracking().FirstOrDefaultAsync(
            x => (x.SourceServiceUrl == url && x.SourceBucketName == profile.BucketName)
                || (x.State != "applied" && (x.ProfileIdsJson.Contains(profileToken)
                    || (x.TargetServiceUrl == url && x.TargetBucketName == profile.BucketName))), ct);
        if (barrier is not null && (admission || barrier.State != "draining"
            || barrier.SourceServiceUrl != url || barrier.SourceBucketName != profile.BucketName))
            throw new StorageMigrationPausedException();
    }

    public async Task<StorageCutoverStatus> BeginAsync(StorageCutoverRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.MigrationId, out _) || request.ProfileIds.Count == 0
            || request.TargetConnectionHash.Length != 64 || !request.TargetConnectionHash.All(Uri.IsHexDigit))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректная миграция."));
        var sourceUrl = NormalizeEndpoint(request.SourceServiceUrl, profiles.GetAllProfiles().Select(x => x.Profile)
            .FirstOrDefault(x => request.ProfileIds.Contains(x.ProfileId))?.IsR2 == true);
        var runtime = profiles.GetAllProfiles().Select(x => x.Profile).Where(x =>
            NormalizeEndpoint(x.ServiceUrl, x.IsR2) == sourceUrl && x.BucketName == request.SourceBucketName).ToArray();
        if (!request.ProfileIds.ToHashSet(StringComparer.Ordinal).SetEquals(runtime.Select(x => x.ProfileId)))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Files использует другую конфигурацию исходного бакета. Перезапустите Files до миграции."));
        await using var lease = await OpenLeaseAsync(sourceUrl, request.SourceBucketName, true, ct);
        var current = await lease.Context.StorageCutovers.FindAsync([request.MigrationId], ct);
        if (current is not null)
            return await StatusAsync(lease.Context, current, ct);
        var previous = await lease.Context.StorageCutovers.Where(x => x.SourceServiceUrl == sourceUrl
            && x.SourceBucketName == request.SourceBucketName).ToArrayAsync(ct);
        if (previous.Any(x => x.State != "applied"))
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Переключение этого бакета уже выполняется."));
        lease.Context.StorageCutovers.RemoveRange(previous);
        var barrier = new StorageCutover
        {
            MigrationId = request.MigrationId, SourceServiceUrl = sourceUrl,
            SourceBucketName = request.SourceBucketName, ProfileIdsJson = JsonSerializer.Serialize(request.ProfileIds.ToArray()),
            TargetServiceUrl = NormalizeEndpoint(request.TargetServiceUrl, request.TargetIsR2), TargetBucketName = request.TargetBucketName,
            TargetRegion = request.TargetRegion, TargetForcePathStyle = request.TargetForcePathStyle,
            TargetIsR2 = request.TargetIsR2, TargetConnectionHash = request.TargetConnectionHash, CreatedAt = DateTime.UtcNow
        };
        lease.Context.StorageCutovers.Add(barrier);
        await lease.Context.SaveChangesAsync(ct);
        await lease.CommitAsync(ct);
        return await StatusAsync(lease.Context, barrier, ct);
    }

    public async Task<StorageCutoverStatus> FreezeAsync(string id, CancellationToken ct)
    {
        var barrier = await FindAsync(id, ct);
        await using var lease = await OpenLeaseAsync(barrier.SourceServiceUrl, barrier.SourceBucketName, true, ct);
        barrier = await lease.Context.StorageCutovers.SingleAsync(x => x.MigrationId == id, ct);
        await PruneActivitiesAsync(lease.Context, ct);
        var status = await StatusAsync(lease.Context, barrier, ct);
        if (barrier.State == "draining" && status.ActiveUploads == 0)
        {
            barrier.State = "frozen";
            await lease.Context.SaveChangesAsync(ct);
            await lease.CommitAsync(ct);
            status.State = "frozen";
        }
        return status;
    }

    public async Task<StorageCutoverStatus> MarkApplyingAsync(string id, CancellationToken ct)
    {
        var barrier = await FindAsync(id, ct);
        await using var lease = await OpenLeaseAsync(barrier.SourceServiceUrl, barrier.SourceBucketName, true, ct);
        barrier = await lease.Context.StorageCutovers.SingleAsync(x => x.MigrationId == id, ct);
        if (barrier.State is not ("frozen" or "applying"))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Бакет ещё не заблокирован."));
        barrier.State = "applying";
        await lease.Context.SaveChangesAsync(ct);
        await lease.CommitAsync(ct);
        return await StatusAsync(lease.Context, barrier, ct);
    }

    public async Task<StorageCutoverStatus> CancelAsync(string id, CancellationToken ct)
    {
        var barrier = await FindAsync(id, ct);
        await using var lease = await OpenLeaseAsync(barrier.SourceServiceUrl, barrier.SourceBucketName, true, ct);
        barrier = await lease.Context.StorageCutovers.SingleAsync(x => x.MigrationId == id, ct);
        var status = await StatusAsync(lease.Context, barrier, ct);
        // Web verifies Configuration still points to the source before calling this service RPC.
        lease.Context.StorageCutovers.Remove(barrier);
        await lease.Context.SaveChangesAsync(ct);
        await lease.CommitAsync(ct);
        status.State = "cancelled";
        return status;
    }

    public async Task<StorageCutoversResponse> GetAsync(string? id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesContext>();
        var query = db.StorageCutovers.AsNoTracking();
        if (!string.IsNullOrEmpty(id)) query = query.Where(x => x.MigrationId == id);
        var response = new StorageCutoversResponse();
        foreach (var barrier in await query.ToArrayAsync(ct))
            response.Cutovers.Add(await StatusAsync(db, barrier, ct));
        return response;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await PruneActivitiesAsync(db, ct);
        foreach (var barrier in await db.StorageCutovers.ToArrayAsync(ct))
        {
            var ids = JsonSerializer.Deserialize<string[]>(barrier.ProfileIdsJson)!;
            if (barrier.State == "applying" && ids.All(id =>
            {
                var profile = profiles.GetProfile(id);
                return NormalizeEndpoint(profile.ServiceUrl, profile.IsR2) == barrier.TargetServiceUrl
                    && profile.BucketName == barrier.TargetBucketName && profile.Region == barrier.TargetRegion
                    && profile.ForcePathStyle == barrier.TargetForcePathStyle && profile.IsR2 == barrier.TargetIsR2
                    && S3Endpoint.ConnectionHash(profile.ServiceUrl, profile.BucketName, profile.AccessKey, profile.SecretKey,
                        profile.Region, profile.ForcePathStyle, profile.IsR2) == barrier.TargetConnectionHash;
            }))
                barrier.State = "applied";
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<StorageCutover> FindAsync(string id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FilesContext>().StorageCutovers
            .AsNoTracking().SingleOrDefaultAsync(x => x.MigrationId == id, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Переключение не найдено."));
    }

    private async Task<StorageCutoverStatus> StatusAsync(FilesContext db, StorageCutover barrier, CancellationToken ct)
    {
        var ids = JsonSerializer.Deserialize<string[]>(barrier.ProfileIdsJson)!;
        var activities = await db.StorageWriteActivities.AsNoTracking().ToArrayAsync(ct);
        var sessions = await db.UploadSessions.AsNoTracking().Where(x =>
            x.Status == UploadSessionStatus.Uploading || x.Status == UploadSessionStatus.Processing || x.CleanupPending).ToArrayAsync(ct);
        var activeSessions = sessions.Where(x => UploadProfiles(x.StorageProfileId, x.FileName, x.ContentType).Intersect(ids).Any())
            .Select(x => x.FileId).ToArray();
        var status = new StorageCutoverStatus
        {
            MigrationId = barrier.MigrationId, State = barrier.State,
            ActiveUploads = activeSessions.Length + activities.Count(x => (!x.FileId.HasValue || !activeSessions.Contains(x.FileId.Value))
                && JsonSerializer.Deserialize<string[]>(x.ProfileIdsJson)!.Intersect(ids).Any()),
            SourceServiceUrl = barrier.SourceServiceUrl, SourceBucketName = barrier.SourceBucketName,
            TargetServiceUrl = barrier.TargetServiceUrl, TargetBucketName = barrier.TargetBucketName,
            TargetRegion = barrier.TargetRegion, TargetForcePathStyle = barrier.TargetForcePathStyle,
            TargetIsR2 = barrier.TargetIsR2
        };
        status.ProfileIds.AddRange(ids);
        return status;
    }

    public static string NormalizeEndpoint(string value, bool isR2 = false) => S3Endpoint.Normalize(value, isR2);

    private static long ActivityKey(string id) => BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("barkcloud-upload:" + id)));

    private async Task<MutationLease> OpenActivityLockAsync(string id, CancellationToken ct)
    {
        var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesContext>();
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var key = ActivityKey(id);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
            return new(scope, db, transaction, null);
        }
        catch { await transaction.DisposeAsync(); scope.Dispose(); throw; }
    }

    private static async Task PruneActivitiesAsync(FilesContext db, CancellationToken ct)
    {
        foreach (var activity in await db.StorageWriteActivities.AsNoTracking().ToArrayAsync(ct))
        {
            var alive = LocalActivities.ContainsKey(activity.Id);
            if (db.Database.IsNpgsql())
            {
                var key = ActivityKey(activity.Id);
                alive = !await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(ct);
            }
            if (!alive) await db.StorageWriteActivities.Where(x => x.Id == activity.Id).ExecuteDeleteAsync(ct);
        }
    }

    public sealed class UploadActivity : IAsyncDisposable
    {
        private readonly StorageMigrationGate _owner;
        private readonly IAsyncDisposable? _lifetime;
        private int _disposed;
        public string Id { get; }
        internal UploadActivity(StorageMigrationGate owner, string id, IAsyncDisposable? lifetime)
        { _owner = owner; Id = id; _lifetime = lifetime; }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _owner.RemoveActivityAsync(Id);
            }
            finally
            {
                LocalActivities.TryRemove(Id, out _);
                if (_lifetime is not null) await _lifetime.DisposeAsync();
            }
        }
    }

    private async Task RemoveActivityAsync(string id)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FilesContext>().StorageWriteActivities.Where(x => x.Id == id).ExecuteDeleteAsync(cleanup.Token);
    }

    private async Task<MutationLease> OpenLeaseAsync(string url, string bucket, bool exclusive, CancellationToken ct)
    {
        var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesContext>();
        var isPostgres = db.Database.IsNpgsql();
        IDbContextTransaction? transaction = null;
        var localAcquired = false;
        try
        {
            if (!isPostgres) { await _localLock.WaitAsync(ct); localAcquired = true; }
            if (isPostgres || exclusive) transaction = await db.Database.BeginTransactionAsync(ct);
            if (isPostgres)
            {
                var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
                    NormalizeEndpoint(url).ToLowerInvariant() + "\n" + bucket)));
                if (exclusive)
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
                else
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared({key})", ct);
            }
            return new MutationLease(scope, db, transaction, localAcquired ? _localLock : null);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            scope.Dispose();
            if (localAcquired) _localLock.Release();
            throw;
        }
    }

    private sealed class AdmissionLease(List<IAsyncDisposable> leases) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        { foreach (var lease in leases.AsEnumerable().Reverse()) await lease.DisposeAsync(); }
    }

    private sealed class MutationLease(IServiceScope scope, FilesContext context,
        IDbContextTransaction? transaction, SemaphoreSlim? localLock) : IAsyncDisposable
    {
        public FilesContext Context => context;
        public Task CommitAsync(CancellationToken ct) => transaction?.CommitAsync(ct) ?? Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); scope.Dispose(); }
            finally { localLock?.Release(); }
        }
    }
}
