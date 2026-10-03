using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Amazon.S3;
using Amazon.S3.Model;

using BarkCloud.GrpcServer;
using BarkCloud.Proto.Configuration;
using BarkCloud.Proto.Files;

using Grpc.Core;

namespace BarkCloud.Web.Infrastructure;

/// <summary>One Web-owned worker; HTTP requests only enqueue work and return sanitized snapshots.</summary>
public sealed class StorageMigrationService(IStorageMigrationControl control, MigrationS3ClientFactory clients,
    S3MigrationCopier copier, ILogger<StorageMigrationService> logger) : BackgroundService
{
    private sealed record Prepared(MigrationSource Source, MigrationConnection SourceConnection,
        MigrationConnection Destination, string Actor, DateTime ExpiresAt);
    private readonly object _commands = new();
    private readonly Dictionary<string, Prepared> _prepared = new();
    private readonly ConcurrentDictionary<string, StorageMigrationJob> _jobs = new();
    private readonly Channel<StorageMigrationJob> _queue = System.Threading.Channels.Channel.CreateUnbounded<StorageMigrationJob>();
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "barkcloud-migrations", Guid.NewGuid().ToString("N"));
    private string? _runningId;

    public async Task<IReadOnlyList<MigrationSource>> GetSourcesAsync(CancellationToken ct = default) =>
        Group(await control.GetProfilesAsync(ct)).Select(x => x.Source).ToArray();

    public async Task<MigrationCheckResult> CheckAsync(string sourceId, MigrationConnection destination, string actor, CancellationToken ct)
    {
        var target = S3MigrationCopier.Normalize(destination);
        var profiles = await control.GetProfilesAsync(ct);
        var source = Group(profiles).SingleOrDefault(x => x.Source.Id == sourceId);
        if (source.Source is null) throw new InvalidOperationException("Выберите настроенный исходный бакет.");
        if (SameLocation(source.Connection, target))
            throw new InvalidOperationException("Нельзя копировать бакет в самого себя.");
        if (profiles.Any(x => SameLocation(MigrationConnection.FromProfile(x), target)))
            throw new InvalidOperationException("Назначение уже используется профилем BarkCloud. Выберите отдельный пустой бакет.");
        lock (_commands)
        {
            if (_jobs.Values.Any(x => SameLocation(x.Destination, target) && x.State is not ("cancelled" or "completed")))
                throw new InvalidOperationException("Этот целевой бакет уже выделен для другой миграции.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try { await copier.CheckAsync(target, deadline.Token); }
        catch (Exception e) when (e is not InvalidOperationException) { throw new InvalidOperationException(SafeError(e)); }
        var id = Guid.NewGuid().ToString();
        lock (_commands)
        {
            foreach (var expired in _prepared.Where(x => x.Value.ExpiresAt < DateTime.UtcNow).Select(x => x.Key).ToArray())
                _prepared.Remove(expired);
            if (_prepared.Count >= 100) throw new InvalidOperationException("Слишком много проверенных подключений. Повторите позже.");
            _prepared[id] = new(source.Source, source.Connection, target, actor, DateTime.UtcNow.AddMinutes(15));
        }
        return new(id, "Доступ, запись, чтение и удаление проверены. Тестовый объект удалён.");
    }

    public MigrationJobSnapshot Start(string validationId, string actor)
    {
        lock (_commands)
        {
            RequireIdle();
            if (!_prepared.TryGetValue(validationId, out var prepared) || prepared.ExpiresAt <= DateTime.UtcNow || prepared.Actor != actor)
                throw new InvalidOperationException("Проверка подключения устарела. Повторите проверку.");
            if (_jobs.Values.Any(x => SameLocation(x.Destination, prepared.Destination) && x.State is not ("cancelled" or "completed")))
                throw new InvalidOperationException("Этот целевой бакет уже выделен для другой миграции.");
            _prepared.Remove(validationId);
            var job = new StorageMigrationJob(prepared.Source, prepared.SourceConnection, prepared.Destination, actor, _workDirectory);
            _jobs[job.Id] = job;
            Enqueue(job, "copy");
            return job.Snapshot();
        }
    }

    public IReadOnlyList<MigrationJobSnapshot> GetJobs() => _jobs.Values.OrderByDescending(x => x.CreatedAt).Select(x => x.Snapshot()).ToArray();
    public MigrationJobSnapshot GetJob(string id) => Find(id).Snapshot();

    public MigrationJobSnapshot Retry(string id)
    {
        lock (_commands)
        {
            RequireIdle();
            var job = Find(id);
            if (job.State != "failed") throw new InvalidOperationException("Продолжить можно только задачу с ошибкой.");
            job.Cancellation.Dispose();
            job.Cancellation = new();
            Enqueue(job, job.Operation);
            return job.Snapshot();
        }
    }

    public async Task<MigrationJobSnapshot> ApplyAsync(string id, CancellationToken ct)
    {
        // No write barrier is acquired until maintenance can actually restart Files.
        await control.PreflightAsync(ct);
        lock (_commands)
        {
            RequireIdle();
            var job = Find(id);
            if (job.State != "copied") throw new InvalidOperationException("Сначала завершите копирование бакета.");
            Enqueue(job, "apply");
            return job.Snapshot();
        }
    }

    public async Task<MigrationJobSnapshot> CancelAsync(string id, CancellationToken ct)
    {
        var job = Find(id);
        lock (_commands)
        {
            if (_runningId == id)
            {
                if (job.PointOfNoReturn) throw new InvalidOperationException("Конфигурация применяется. Дождитесь результата переключения.");
                job.Update(() => job.State = "stopping");
                job.Cancellation.Cancel();
                return job.Snapshot();
            }
            RequireIdle();
            _runningId = id;
        }
        try
        {
            if (job.BarrierStarted)
            {
                await CancelBarrierSafelyAsync(job.Id, job.Source, ct);
                job.Update(() => { job.BarrierStarted = false; job.PointOfNoReturn = false; job.State = "copied"; job.Error = null; job.FinalSyncDone = false; });
                ResetFinalPass(job);
                RestoreCopiedSnapshot(job);
            }
            else job.Update(() => job.State = "cancelled");
            return job.Snapshot();
        }
        finally { lock (_commands) _runningId = null; }
    }

    public async Task<IReadOnlyList<MigrationCutover>> GetCutoversAsync(CancellationToken ct)
    {
        var barriers = await control.GetCutoversAsync(ct);
        var profiles = await control.GetProfilesAsync(ct);
        return barriers.Where(x => x.State != "applied").Select(x => new MigrationCutover(x.MigrationId, x.State,
            SourceFor(x, profiles), new(x.TargetServiceUrl, x.TargetBucketName, x.TargetIsR2, x.TargetRegion, x.TargetForcePathStyle),
            x.ActiveUploads, MatchesSource(x, profiles), MatchesTarget(x, profiles))).ToArray();
    }

    public async Task CancelCutoverAsync(string id, CancellationToken ct)
    {
        if (_jobs.TryGetValue(id, out var job) && job.BarrierStarted)
        {
            await CancelAsync(id, ct);
            return;
        }
        lock (_commands) { RequireIdle(); _runningId = id; }
        try
        {
            var barrier = (await control.GetCutoversAsync(ct)).SingleOrDefault(x => x.MigrationId == id)
                ?? throw new InvalidOperationException("Переключение не найдено.");
            await CancelBarrierSafelyAsync(id, SourceFor(barrier, await control.GetProfilesAsync(ct)), ct);
        }
        finally { lock (_commands) _runningId = null; }
    }

    public async Task<MigrationJobSnapshot> RecoverCutoverAsync(string id, string actor, CancellationToken ct)
    {
        await control.PreflightAsync(ct);
        var barrier = (await control.GetCutoversAsync(ct)).SingleOrDefault(x => x.MigrationId == id)
            ?? throw new InvalidOperationException("Переключение не найдено.");
        var profiles = await control.GetProfilesAsync(ct);
        if (!MatchesTarget(barrier, profiles))
            throw new InvalidOperationException("Новые подключения ещё не сохранены. Доступна безопасная отмена переключения.");
        lock (_commands)
        {
            RequireIdle();
            if (_jobs.TryGetValue(id, out var existing))
            {
                if (existing.State != "failed") throw new InvalidOperationException("Переключение уже выполняется.");
                existing.Cancellation.Dispose();
                existing.Cancellation = new();
                existing.ConfigurationApplied = true;
                Enqueue(existing, "apply");
                return existing.Snapshot();
            }
            var target = MigrationConnection.FromProfile(profiles.First(x => barrier.ProfileIds.Contains(x.ProfileId)));
            var source = SourceFor(barrier, profiles);
            var job = new StorageMigrationJob(source, target with { ServiceUrl = source.ServiceUrl, BucketName = source.BucketName }, target, actor, _workDirectory, id)
            { BarrierStarted = true, ConfigurationApplied = true, PointOfNoReturn = true, FinalSyncDone = true };
            _jobs[id] = job;
            Enqueue(job, "apply");
            return job.Snapshot();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
            try
            {
                job.Update(() => { job.State = "running"; job.Error = null; });
                if (job.Operation == "copy") await CopyPassAsync(job, false, linked.Token);
                else await ApplyJobAsync(job, linked.Token, stoppingToken);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (job.BarrierStarted)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await CancelBarrierSafelyAsync(job.Id, job.Source, cleanup.Token);
                        job.Update(() => { job.BarrierStarted = false; job.State = "copied"; job.PointOfNoReturn = false; job.FinalSyncDone = false; });
                        ResetFinalPass(job);
                        RestoreCopiedSnapshot(job);
                    }
                    else job.Update(() => job.State = "cancelled");
                }
                catch (Exception e) { Fail(job, e); }
            }
            catch (Exception e) { Fail(job, e); }
            finally
            {
                job.Update(() => job.CurrentBytes = 0);
                lock (_commands) _runningId = null;
            }
        }
    }

    private async Task CopyPassAsync(StorageMigrationJob job, bool final, CancellationToken ct)
    {
        Directory.CreateDirectory(job.Directory);
        var inventory = Path.Combine(job.Directory, final ? "final-inventory.ndjson" : "inventory.ndjson");
        var confirmed = Path.Combine(job.Directory, final ? "final-confirmed.ndjson" : "confirmed.ndjson");
        using var source = clients.Create(job.SourceConnection);
        using var target = clients.Create(job.Destination);
        var complete = final ? job.FinalInventoryComplete : job.InventoryComplete;
        if (!complete)
        {
            if (!final)
            {
                await AssertSourceAsync(job, ct);
                if ((await control.GetCutoversAsync(ct)).Any(x => x.State != "applied"))
                    throw new InvalidOperationException("Сначала завершите или отмените незавершённое переключение.");
                if (SameLocation(job.SourceConnection, job.Destination)) throw new InvalidOperationException("Нельзя копировать бакет в самого себя.");
                await S3MigrationCopier.EnsureEmptyAsync(target, job.Destination.BucketName, ct);
            }
            job.Update(() => { job.Phase = final ? "final-counting" : "counting"; job.TotalBytes = 0; job.TotalFiles = 0; job.CurrentKey = null; job.CurrentName = null; });
            await using (var writer = new StreamWriter(inventory, false, Encoding.UTF8))
            {
                string? previousKey = null;
                await foreach (var item in S3MigrationCopier.ListAsync(source, job.Source.BucketName, ct))
                {
                    if (previousKey is not null && CompareKeys(previousKey, item.Key) >= 0)
                        throw new InvalidOperationException("S3 должен перечислять уникальные ключи в порядке UTF-8.");
                    previousKey = item.Key;
                    await writer.WriteLineAsync(JsonSerializer.Serialize(item).AsMemory(), ct);
                    job.Update(() => { job.TotalBytes = checked(job.TotalBytes + item.Size); job.TotalFiles++; });
                }
                await writer.FlushAsync(ct);
            }
            job.Update(() => { if (final) job.FinalInventoryComplete = true; else job.InventoryComplete = true; });
        }
        await using var output = new FileStream(confirmed, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        output.SetLength(job.ConfirmedManifestLength);
        output.Position = output.Length;
        using var saved = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        using var reader = new StreamReader(inventory, Encoding.UTF8);
        using var previous = final ? new StreamReader(Path.Combine(job.Directory, "confirmed.ndjson"), Encoding.UTF8) : null;
        var old = previous is null ? null : await ReadObjectAsync(previous, ct);
        long index = 0;
        while (await ReadObjectAsync(reader, ct) is { } item)
        {
            if (index++ < job.ProcessedObjects) continue;
            ct.ThrowIfCancellationRequested();
            job.Update(() => { job.CurrentKey = item.Key; job.CurrentName = null; job.CurrentBytes = 0; job.Phase = final ? "final-copying" : "copying"; });
            while (old is not null && CompareKeys(old.Key, item.Key) < 0) old = await ReadObjectAsync(previous!, ct);
            MigrationObject result;
            if (old is not null && old.Key == item.Key && await UnchangedAsync(source, job.Source.BucketName, old, item, ct)) result = old;
            else
            {
                try
                {
                    result = await CopyCurrentAsync(source, target, job, item, final, ct);
                }
                catch (MigrationSourceMissingException) when (!final)
                {
                    // Live writes may delete an object after inventory; final inventory is authoritative.
                    job.Update(() => { job.TotalFiles--; job.TotalBytes -= item.Size; job.ProcessedObjects++; });
                    continue;
                }
            }
            await saved.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), ct);
            await saved.FlushAsync(ct);
            job.Update(() =>
            {
                job.ConfirmedManifestLength = output.Position;
                job.ProcessedObjects++; job.CopiedFiles++; job.CopiedBytes += result.Size;
                job.TotalBytes += result.Size - item.Size; job.CurrentBytes = 0;
            });
        }
        job.Update(() =>
        {
            job.CurrentKey = null; job.CurrentName = null; job.CurrentBytes = 0;
            if (!final)
            {
                job.State = "copied"; job.Phase = "copied";
                job.BaseCopiedBytes = job.CopiedBytes; job.BaseCopiedFiles = job.CopiedFiles;
                job.BaseTotalBytes = job.TotalBytes; job.BaseTotalFiles = job.TotalFiles;
            }
        });
    }

    private async Task<MigrationObject> CopyCurrentAsync(IAmazonS3 source, IAmazonS3 target, StorageMigrationJob job,
        MigrationObject item, bool final, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await copier.CopyAsync(source, job.SourceConnection, target, job.Destination, item, job.Directory,
                    progress => job.Update(() => { job.CurrentBytes = progress.UploadedBytes; job.Phase = final ? "final-" + progress.Phase : progress.Phase; job.CurrentName = progress.FileName; }), ct);
            }
            catch (AmazonS3Exception e) when (!final && e.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 3)
            {
                GetObjectMetadataResponse current;
                try { current = await source.GetObjectMetadataAsync(job.Source.BucketName, item.Key, ct); }
                catch (AmazonS3Exception missing) when (missing.StatusCode == HttpStatusCode.NotFound) { throw new MigrationSourceMissingException(); }
                item = new(item.Key, current.ContentLength, current.ETag ?? "", current.LastModified);
            }
        }
    }

    private async Task ApplyJobAsync(StorageMigrationJob job, CancellationToken ct, CancellationToken stoppingToken)
    {
        await control.PreflightAsync(ct);
        if (!job.ConfigurationApplied && job.PointOfNoReturn)
        {
            var profiles = await control.GetProfilesAsync(ct);
            if (job.Source.ProfileIds.All(id => profiles.Any(p => p.ProfileId == id && MigrationConnection.FromProfile(p) == job.Destination)))
            {
                var barrier = (await control.GetCutoversAsync(ct)).SingleOrDefault(x => x.MigrationId == job.Id)
                    ?? throw new InvalidOperationException("Барьер переключения не найден. Проверьте состояние Files.");
                if (barrier.State != "applied") await control.MarkApplyingAsync(job.Id, stoppingToken);
                job.ConfigurationApplied = true;
            }
        }
        if (!job.ConfigurationApplied)
        {
            if (!job.PointOfNoReturn)
            {
                await AssertSourceAsync(job, ct);
                job.Update(() => job.Phase = "draining");
                // Set before RPC: a lost reply may still have created the durable barrier.
                job.BarrierStarted = true;
                await control.BeginAsync(job.Id, job.Source, job.Destination, ct);
                while (true)
                {
                    var status = await control.FreezeAsync(job.Id, ct);
                    job.Update(() => job.ActiveUploads = status.ActiveUploads);
                    if (status.State is "frozen" or "applying") break;
                    await Task.Delay(1000, ct);
                }
                if (!job.FinalSyncDone)
                {
                    if (!job.FinalInventoryComplete && job.Phase == "draining") ResetFinalPass(job);
                    await CopyPassAsync(job, true, ct);
                    job.FinalSyncDone = true;
                }
                ct.ThrowIfCancellationRequested();
                job.Update(() => { job.PointOfNoReturn = true; job.Phase = "applying"; });
            }
            // Once saving begins, user cancellation cannot open the old bucket after a lost reply.
            await control.MarkApplyingAsync(job.Id, stoppingToken);
            await control.RelocateAsync(job.Id, job.Source, job.Destination, job.Actor, stoppingToken);
            job.ConfigurationApplied = true;
        }
        job.Update(() => job.Phase = "restarting");
        await control.RestartAndVerifyAsync(job.Id, stoppingToken);
        job.Update(() => { job.State = "completed"; job.Phase = "completed"; job.ActiveUploads = 0; });
    }

    private static void ResetFinalPass(StorageMigrationJob job)
    {
        job.Update(() =>
        {
            job.FinalInventoryComplete = false; job.ProcessedObjects = 0; job.ConfirmedManifestLength = 0;
            job.CopiedBytes = 0; job.CopiedFiles = 0; job.CurrentBytes = 0;
        });
    }

    private static void RestoreCopiedSnapshot(StorageMigrationJob job) => job.Update(() =>
    {
        job.Phase = "copied"; job.CopiedBytes = job.BaseCopiedBytes; job.CopiedFiles = job.BaseCopiedFiles;
        job.TotalBytes = job.BaseTotalBytes; job.TotalFiles = job.BaseTotalFiles; job.ActiveUploads = 0;
    });

    private async Task AssertSourceAsync(StorageMigrationJob job, CancellationToken ct)
    {
        var current = Group(await control.GetProfilesAsync(ct)).SingleOrDefault(x => x.Source.Id == job.Source.Id);
        if (current.Source is null || !current.Source.ProfileIds.ToHashSet(StringComparer.Ordinal).SetEquals(job.Source.ProfileIds)
            || current.Connection != job.SourceConnection)
            throw new InvalidOperationException("Подключения исходного бакета изменились после проверки. Начните новую миграцию.");
    }

    private async Task CancelBarrierSafelyAsync(string id, MigrationSource source, CancellationToken ct)
    {
        var profiles = await control.GetProfilesAsync(ct);
        var ids = source.ProfileIds.ToHashSet(StringComparer.Ordinal);
        if (!ids.SetEquals(profiles.Where(x => SameLocation(S3Endpoint.Normalize(x.ServiceUrl, x.IsR2), x.BucketName, source.ServiceUrl, source.BucketName)).Select(x => x.ProfileId)))
            throw new InvalidOperationException("Подключения уже изменились. Запись на старый S3 не открыта; повторите перезапуск Files.");
        var barriers = await control.GetCutoversAsync(ct);
        if (barriers.Any(x => x.MigrationId == id && x.State != "applied")) await control.CancelAsync(id, ct);
    }

    private void Enqueue(StorageMigrationJob job, string operation)
    {
        job.Update(() => { job.Operation = operation; job.State = "queued"; job.Error = null; });
        _runningId = job.Id;
        _queue.Writer.TryWrite(job);
    }
    private void RequireIdle() { if (_runningId is not null) throw new InvalidOperationException("Другая миграция уже выполняется."); }
    private StorageMigrationJob Find(string id) => _jobs.TryGetValue(id, out var job) ? job : throw new InvalidOperationException("Задача не найдена. После перезапуска Web временные задачи не восстанавливаются.");
    private void Fail(StorageMigrationJob job, Exception error)
    {
        job.Update(() => { job.State = "failed"; job.Error = SafeError(error); job.CurrentBytes = 0; });
        logger.LogWarning("Миграция {MigrationId} остановлена на этапе {Phase}", job.Id, job.Phase);
    }

    private static async Task<MigrationObject?> ReadObjectAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        return line is null ? null : JsonSerializer.Deserialize<MigrationObject>(line)!;
    }
    private static int CompareKeys(string a, string b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
    private static async Task<bool> UnchangedAsync(IAmazonS3 source, string bucket, MigrationObject a, MigrationObject b, CancellationToken ct)
    {
        if (a.Size != b.Size || a.Etag != b.Etag || a.LastModified != b.LastModified || a.Sha256 is null || a.MetadataHash is null) return false;
        var metadata = await source.GetObjectMetadataAsync(bucket, b.Key, ct);
        return metadata.ContentLength == b.Size && metadata.ETag == b.Etag
            && S3MigrationCopier.MetadataHash(metadata.Metadata, metadata.Headers) == a.MetadataHash;
    }
    public static bool SameLocation(string aUrl, string aBucket, string bUrl, string bBucket) =>
        string.Equals(NormalizeEndpoint(aUrl), NormalizeEndpoint(bUrl), StringComparison.OrdinalIgnoreCase) && aBucket == bBucket;
    private static bool SameLocation(MigrationConnection a, MigrationConnection b) => SameLocation(
        EffectiveEndpoint(a), a.BucketName, EffectiveEndpoint(b), b.BucketName);
    private static string EffectiveEndpoint(MigrationConnection c) => S3Endpoint.Normalize(c.ServiceUrl, c.IsR2);
    private static string NormalizeEndpoint(string value) => S3Endpoint.Normalize(value);
    private static IReadOnlyList<(MigrationSource Source, MigrationConnection Connection)> Group(IReadOnlyList<StorageProfileItem> profiles) =>
        profiles.GroupBy(x => S3Endpoint.Normalize(x.ServiceUrl, x.IsR2).ToLowerInvariant() + "\n" + x.BucketName).Select(group =>
        {
            var first = group.OrderByDescending(x => x.IsActive).ThenByDescending(x => x.Version).ThenBy(x => x.ProfileId, StringComparer.Ordinal).First();
            var connection = MigrationConnection.FromProfile(first) with { ServiceUrl = S3Endpoint.Normalize(first.ServiceUrl, first.IsR2) };
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key)));
            return (new MigrationSource(id, connection.ServiceUrl, connection.BucketName,
                group.Select(x => x.Role).Distinct().Order().ToArray(), group.Select(x => x.ProfileId).Order().ToArray()), connection);
        }).OrderBy(x => x.Item1.BucketName).ToArray();
    private static MigrationSource SourceFor(StorageCutoverStatus b, IReadOnlyList<StorageProfileItem> profiles) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeEndpoint(b.SourceServiceUrl).ToLowerInvariant() + "\n" + b.SourceBucketName))),
        b.SourceServiceUrl, b.SourceBucketName, profiles.Where(x => b.ProfileIds.Contains(x.ProfileId)).Select(x => x.Role).Distinct().ToArray(), b.ProfileIds.ToArray());
    private static bool MatchesSource(StorageCutoverStatus b, IReadOnlyList<StorageProfileItem> profiles) =>
        b.ProfileIds.ToHashSet().SetEquals(profiles.Where(x => SameLocation(S3Endpoint.Normalize(x.ServiceUrl, x.IsR2), x.BucketName, b.SourceServiceUrl, b.SourceBucketName)).Select(x => x.ProfileId));
    private static bool MatchesTarget(StorageCutoverStatus b, IReadOnlyList<StorageProfileItem> profiles) =>
        b.ProfileIds.Count > 0 && b.ProfileIds.All(id => profiles.Any(x => x.ProfileId == id
            && SameLocation(S3Endpoint.Normalize(x.ServiceUrl, x.IsR2), x.BucketName, b.TargetServiceUrl, b.TargetBucketName)
            && x.Region == b.TargetRegion && (!x.HasForcePathStyle || x.ForcePathStyle) == b.TargetForcePathStyle && x.IsR2 == b.TargetIsR2));

    public static string SafeError(Exception e) => e switch
    {
        AmazonS3Exception s3 when s3.StatusCode == HttpStatusCode.Forbidden => "S3 отказал в доступе. Проверьте credentials и права чтения, записи и удаления.",
        AmazonS3Exception s3 when s3.StatusCode == HttpStatusCode.NotFound => "Бакет или объект не найден на S3.",
        MigrationSourceMissingException => "Объект источника исчез во время финальной сверки. Конфигурация не изменена.",
        AmazonS3Exception s3 when s3.StatusCode == HttpStatusCode.PreconditionFailed => "Объект источника изменился во время копирования. Повторите передачу.",
        AmazonS3Exception => "Ошибка S3 или сети. Проверьте доступность хранилища и продолжите задачу.",
        OperationCanceledException => "Операция прервана или превышено время ожидания. Можно повторить.",
        RpcException => "Сервис недоступен или отклонил переключение. Проверьте подключения и раздел «Обслуживание».",
        InvalidOperationException => e.Message,
        _ => "Не удалось завершить операцию. Проверьте доступность сервисов и продолжите задачу."
    };

    public override void Dispose()
    {
        base.Dispose();
        foreach (var job in _jobs.Values) job.Cancellation.Dispose();
        if (Directory.Exists(_workDirectory)) Directory.Delete(_workDirectory, true);
    }
}
