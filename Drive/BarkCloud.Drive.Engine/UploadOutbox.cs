using System.Text.Json;

using BarkCloud.Drive.Contracts.Localization;

using Grpc.Core;

namespace BarkCloud.Drive.Engine;

internal sealed class UploadOutboxEntry
{
    public string Id { get; set; } = "";
    public string DataPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string DirectoryId { get; set; } = "";
    public string? ExistingEntryId { get; set; }
    public string ServerKey { get; set; } = "";
    public string? OwnerUserId { get; set; }
    public PersistStage Stage { get; set; }
    public string? FileId { get; set; }
    public PersistOperation? Op { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public UploadOutboxEntry Copy() => new()
    {
        Id = Id,
        DataPath = DataPath,
        Name = Name,
        DirectoryId = DirectoryId,
        ExistingEntryId = ExistingEntryId,
        ServerKey = ServerKey,
        OwnerUserId = OwnerUserId,
        Stage = Stage,
        FileId = FileId,
        Op = Op is null ? null : BarkCloudFileSystem.CopyOperation(Op),
        Attempts = Attempts,
        LastError = LastError,
        CreatedAt = CreatedAt,
    };
}

internal sealed class UploadOutbox
{
    private readonly object _gate = new();
    private readonly object _fileLock = new();
    private readonly Dictionary<string, UploadOutboxEntry> _entries = new(StringComparer.Ordinal);
    private readonly Action<string, string> _writeText;
    private readonly TimeProvider _time;

    public UploadOutbox(string dir, TimeProvider? time = null)
        : this(dir, File.WriteAllText, time)
    {
    }

    internal UploadOutbox(string dir, Action<string, string> writeText, TimeProvider? time = null)
    {
        DirectoryPath = dir;
        _writeText = writeText;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(DirectoryPath);
        Load();
    }

    public string DirectoryPath { get; }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public IReadOnlyList<UploadOutboxEntry> Entries
    {
        get { lock (_gate) return _entries.Values.Select(entry => entry.Copy()).ToArray(); }
    }

    public UploadOutboxEntry Add(LocalFile file, string serverKey, string? ownerUserId, TimeProvider? time = null)
    {
        file.KeepData = true;
        var id = Guid.NewGuid().ToString("D");
        var binPath = Path.Combine(DirectoryPath, id + ".bin");
        var dataPath = file.TempPath;
        var moved = false;

        try
        {
            File.Move(file.TempPath, binPath);
            dataPath = binPath;
            moved = true;
        }
        catch (Exception ex)
        {
            EngineLog.Error(Loc.T("Eng_OutboxMoveFailedFmt", file.Name), ex);
        }

        var operation = BarkCloudFileSystem.CopyOperation(file.Op ?? new PersistOperation());
        if (file.Modified && operation.Stage == PersistStage.Done)
            operation = new PersistOperation();
        var entry = new UploadOutboxEntry
        {
            Id = id,
            DataPath = dataPath,
            Name = file.Name,
            DirectoryId = file.DirectoryId,
            ExistingEntryId = file.ExistingEntryId,
            ServerKey = serverKey,
            OwnerUserId = ownerUserId,
            Stage = operation.Stage,
            FileId = operation.FileId,
            Op = operation,
            Attempts = 0,
            CreatedAt = (time ?? _time).GetUtcNow(),
        };

        try
        {
            Save(entry);
        }
        catch
        {
            if (moved)
            {
                try
                {
                    File.Move(binPath, file.TempPath);
                }
                catch (Exception rollbackError)
                {
                    file.TempPath = binPath;
                    EngineLog.Error(Loc.T("Eng_OutboxRollbackFailedFmt", file.Name), rollbackError);
                }
            }

            throw;
        }

        file.TempPath = dataPath;
        file.OutboxId = id;
        return entry.Copy();
    }

    public UploadOutboxEntry? Find(string id)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out var entry) ? entry.Copy() : null;
    }

    public void Save(UploadOutboxEntry entry)
    {
        lock (_fileLock)
        {
            var manifestPath = ManifestPath(entry.Id);
            var temporaryPath = manifestPath + ".tmp";
            var json = JsonSerializer.Serialize(entry);
            try
            {
                _writeText(temporaryPath, json);
                File.Move(temporaryPath, manifestPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temporaryPath); } catch { }
                throw;
            }

            lock (_gate)
                _entries[entry.Id] = entry.Copy();
        }
    }

    public void Remove(UploadOutboxEntry entry, Func<bool>? deferDataDeletion = null)
    {
        lock (_fileLock)
        {
            File.Delete(ManifestPath(entry.Id));

            lock (_gate)
                _entries.Remove(entry.Id);

            var keepData = false;
            try { keepData = deferDataDeletion?.Invoke() ?? false; }
            catch (Exception ex)
            {
                keepData = true;
                EngineLog.Error(Loc.T("Eng_OutboxCallbackError"), ex);
            }
            if (!keepData)
            {
                try { File.Delete(entry.DataPath); }
                catch (Exception ex)
                {
                    EngineLog.Error(Loc.T("Eng_OutboxDataDeleteFailedFmt", entry.Name), ex);
                }
            }
            try { File.Delete(ManifestPath(entry.Id) + ".tmp"); } catch { }
        }
    }

    private string ManifestPath(string id) => Path.Combine(DirectoryPath, id + ".json");

    internal void AdoptUnowned(string serverKey, string ownerUserId)
    {
        foreach (var entry in Entries)
        {
            if (entry.ServerKey != serverKey || entry.OwnerUserId is not null)
                continue;
            entry.OwnerUserId = ownerUserId;
            Save(entry);
            EngineLog.Info(Loc.T("Eng_OutboxAdoptedFmt", entry.Name, ownerUserId));
        }
    }

    private void Load()
    {
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<UploadOutboxEntry>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.DataPath))
                    continue;

                entry.Op ??= new PersistOperation { Stage = entry.Stage, FileId = entry.FileId };
                entry.Stage = entry.Op.Stage;
                entry.FileId = entry.Op.FileId;
                lock (_gate)
                    _entries[entry.Id] = entry;
            }
            catch (Exception ex)
            {
                EngineLog.Error(Loc.T("Eng_OutboxManifestReadFailedFmt", path), ex);
            }
        }

        foreach (var binPath in Directory.EnumerateFiles(DirectoryPath, "*.bin"))
        {
            var manifestPath = Path.ChangeExtension(binPath, ".json");
            if (!File.Exists(manifestPath))
                EngineLog.Info(Loc.T("Eng_OutboxOrphanFileFmt", binPath));
        }
    }
}

internal sealed class UploadOutboxWorker : IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    private readonly UploadOutbox _outbox;
    private readonly TimeProvider _time;
    private readonly Func<(string? ServerKey, string? OwnerUserId)> _session;
    private readonly Func<UploadOutboxEntry, Task> _process;
    private readonly Action<UploadOutboxEntry>? _completed;
    private readonly Action<UploadOutboxEntry, Exception>? _failed;
    private readonly Func<UploadOutboxEntry, bool>? _deferDataDeletion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();
    private ITimer? _timer;
    private bool _running;
    private bool _rerunRequested;
    private int _retryIndex;

    public UploadOutboxWorker(UploadOutbox outbox,
        Func<(string? ServerKey, string? OwnerUserId)> session,
        Func<UploadOutboxEntry, Task> process,
        Action<UploadOutboxEntry>? completed = null,
        Action<UploadOutboxEntry, Exception>? failed = null,
        TimeProvider? time = null,
        Func<UploadOutboxEntry, bool>? deferDataDeletion = null)
    {
        _outbox = outbox;
        _session = session;
        _process = process;
        _completed = completed;
        _failed = failed;
        _deferDataDeletion = deferDataDeletion;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        lock (_timerLock)
        {
            if (_running)
                return;
            _running = true;
            _retryIndex = 0;
            _timer?.Change(RetryDelays[0], Timeout.InfiniteTimeSpan);
        }
    }

    public async Task TriggerAsync()
    {
        lock (_timerLock)
        {
            if (!_running)
                return;
        }

        await Task.Yield();
        await RunPassAsync().ConfigureAwait(false);
    }

    public void Trigger()
    {
        _ = Task.Run(async () =>
        {
            try { await TriggerAsync().ConfigureAwait(false); }
            catch (Exception ex) { EngineLog.Error(Loc.T("Eng_OutboxTriggerFailed"), ex); }
        });
    }

    public async Task StopAsync()
    {
        lock (_timerLock)
        {
            _running = false;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    private void OnTimer(object? _)
    {
        Trigger();
    }

    private async Task RunPassAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            lock (_timerLock)
                if (_running)
                    _rerunRequested = true;
            return;
        }

        var failed = false;
        var retryAfterDirectoryNotFound = false;
        try
        {
            bool runAgain;
            do
            {
                lock (_timerLock)
                {
                    if (!_running)
                        break;
                    _rerunRequested = false;
                }

                try
                {
                    var (serverKey, ownerUserId) = _session();
                    if (!string.IsNullOrWhiteSpace(serverKey) && !string.IsNullOrWhiteSpace(ownerUserId))
                    {
                        _outbox.AdoptUnowned(serverKey, ownerUserId);

                        foreach (var snapshot in _outbox.Entries)
                        {
                            lock (_timerLock)
                            {
                                if (!_running)
                                    break;
                            }

                            if (!string.Equals(snapshot.ServerKey, serverKey, StringComparison.Ordinal)
                                || !string.Equals(snapshot.OwnerUserId, ownerUserId, StringComparison.Ordinal))
                                continue;

                            try
                            {
                                await _process(snapshot).ConfigureAwait(false);
                                _outbox.Remove(snapshot, () => _deferDataDeletion?.Invoke(snapshot) ?? false);
                                _completed?.Invoke(snapshot);
                            }
                            catch (Exception ex)
                            {
                                var current = _outbox.Find(snapshot.Id);
                                if (current is null)
                                {
                                    EngineLog.Error(Loc.T("Eng_OutboxCallbackError"), ex);
                                    continue;
                                }

                                failed = true;
                                retryAfterDirectoryNotFound |= HasDirectoryNotFound(ex);
                                current.Attempts++;
                                current.LastError = ex.Message;
                                try { _outbox.Save(current); }
                                catch (Exception saveError) { EngineLog.Error(Loc.T("Eng_OutboxSaveError"), saveError); }
                                try { _failed?.Invoke(current, ex); }
                                catch (Exception callbackError) { EngineLog.Error(Loc.T("Eng_OutboxCallbackError"), callbackError); }
                                EngineLog.Error(Loc.T("Eng_OutboxRetryFmt", snapshot.Name), ex);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    EngineLog.Error(Loc.T("Eng_OutboxPassError"), ex);
                }

                lock (_timerLock)
                    runAgain = _running && _rerunRequested;
            }
            while (runAgain);
        }
        finally
        {
            _gate.Release();
            var runAgain = false;
            lock (_timerLock)
            {
                if (_running)
                {
                    if (!failed)
                        _retryIndex = 0;
                    if (_rerunRequested)
                    {
                        _rerunRequested = false;
                        runAgain = true;
                    }
                    else
                    {
                        var delay = retryAfterDirectoryNotFound
                            ? RetryDelays[^1]
                            : failed ? NextRetryDelay() : TimeSpan.FromMinutes(5);
                        _timer?.Change(delay, Timeout.InfiniteTimeSpan);
                    }
                }
            }
            if (runAgain)
                Trigger();
        }
    }

    private TimeSpan NextRetryDelay()
    {
        var delay = RetryDelays[Math.Min(_retryIndex, RetryDelays.Length - 1)];
        if (_retryIndex < RetryDelays.Length - 1)
            _retryIndex++;
        return delay;
    }

    private static bool HasDirectoryNotFound(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is RpcException rpc && ServerErrors.HasCode(rpc, ServerErrors.DirectoryNotFound))
                return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
