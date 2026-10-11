using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace BarkCloud.Drive.Engine;

// Результат резолва пути "\A\b.txt" в облачные идентификаторы.
internal sealed class ResolvedNode
{
    public bool IsDirectory { get; init; }
    public string DirectoryId { get; init; } = "";  // папка: её id; файл: id родительской папки
    public string EntryId { get; init; } = "";        // файл: CloudFileEntry.Id
    public string FileId { get; init; } = "";          // файл: UploadFile.Id (блоб)
    public string Name { get; init; } = "";
    public long Length { get; init; }
    public DateTime? Created { get; init; }
    public DateTime? Updated { get; init; }
}

// Обёртка над CloudApi + FilesApi: листинги (TTL-кэш), резолв путей, чтение (гидрация
// целиком) и запись (upload по HTTP + привязка/перемещение/удаление через gRPC).
internal sealed class CloudGateway : IDisposable
{
    private readonly record struct ListingLoadKey(string DirectoryId, int Generation, long AccountEpoch);

    private sealed record DirectoryListingIndex(
        Dictionary<string, BarkCloud.Proto.Files.DirectoryInfo> SubdirectoriesByName,
        Dictionary<string, BarkCloud.Proto.Files.DirectoryInfo> SubdirectoriesByExactName,
        Dictionary<string, FileEntryDetailed> FilesByName,
        Dictionary<string, FileEntryDetailed> FilesByExactName);

    private sealed record CachedDirectoryListing(
        DateTime At,
        int Generation,
        long AccountEpoch,
        DirectoryListingDetailed Listing,
        DirectoryListingIndex Index);

    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StorageTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StorageFailureCache = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StorageRpcTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HttpTransferIdleLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RangeBlockTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HttpUploadResponseLimit = TimeSpan.FromMinutes(10);
    private const int DirectoryPageSize = 200;

    private readonly CloudApi.CloudApiClient _cloud;
    private readonly FilesApi.FilesApiClient _files;
    private readonly HttpClient _http;
    private string _cacheDir;
    private readonly string _filesWebBase;
    private readonly Func<string?> _tokenProvider;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, CachedDirectoryListing> _listCache = new();
    private readonly ConcurrentDictionary<string, int> _listGen = new(); // «поколение» инвалидации на папку — защита от записи устаревшего ответа
    private readonly object _listLock = new(); // защищает кэш, поколения и реестр объединяемых загрузок
    private readonly Dictionary<ListingLoadKey, TaskCompletionSource<DirectoryListingDetailed>> _listLoads = new();
    private readonly ConcurrentDictionary<DownloadKey, Lazy<Task<string>>> _downloads = new();
    private readonly object _cacheEpochLock = new();
    private CancellationTokenSource _cacheEpochCancellation = new();
    private long _accountEpoch;
    private readonly object _storageLock = new();
    private (DateTime At, GetUserStorageInfoResponse Info)? _storage;
    private long _storageEpoch;
    private TaskCompletionSource<GetUserStorageInfoResponse>? _storageLoad;
    private long _storageLoadEpoch;
    private DateTime _storageRetryAfter;
    private ExceptionDispatchInfo? _storageFailure;

    // Поблочное чтение (фаза 5): кэш temp-URL на файл (живёт 60 мин — обновляем заранее),
    // дедуп параллельных скачиваний блоков, и файлы, для которых сервер не дал Range (целиком).
    private const int BlockSize = 1 << 20; // 1 МиБ
    private static readonly TimeSpan UrlTtl = TimeSpan.FromMinutes(50);
    private readonly ConcurrentDictionary<string, (long Epoch, DateTime At, string Url)> _tempUrls = new();
    private readonly ConcurrentDictionary<BlockFetchKey, Lazy<Task>> _blockFetches = new();
    private readonly ConcurrentDictionary<string, bool> _wholeMode = new();

    // Батчинг удалений: пользовательские удаления файлов копятся до 1 c (или порога в 100)
    // и уходят одним DeleteFileEntries — ускоряет массовое удаление. Буферизованные записи
    // сразу прячутся из листингов «тумбстонами», чтобы файлы не мелькали в Проводнике до отправки.
    private const int DeleteBatchMax = 100;
    private const int DeleteMaxAttempts = 3; // ретраи пачки при транзиентной ошибке отправки
    private static readonly TimeSpan DeleteWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeleteDeadline = TimeSpan.FromSeconds(5); // gRPC-дедлайн, чтобы выход не висел при недоступном сервере
    private readonly object _delLock = new();   // защищает _delPending и перепланирование _delTimer
    private readonly object _sendLock = new();  // сериализует отправку: фоновую (таймер/порог) и блокирующую (выход)
    private int _ending;
    private readonly List<(string EntryId, string DirId)> _delPending = new();
    private readonly ConcurrentDictionary<string, byte> _delTombstones = new();
    private readonly ConcurrentDictionary<string, int> _delAttempts = new();
    private readonly ITimer _delTimer;

    public Action<string>? SyncError { get; set; }

    internal Action? ListingLoadWaiterJoinedForTesting { get; set; }

    public CloudGateway(CloudApi.CloudApiClient cloud, FilesApi.FilesApiClient files, HttpClient http,
        string filesWebBase, Func<string?> tokenProvider, string cacheDir, TimeProvider? time = null)
    {
        _cloud = cloud;
        _files = files;
        _http = http;
        _filesWebBase = filesWebBase.TrimEnd('/');
        _tokenProvider = tokenProvider;
        _cacheDir = cacheDir;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(_cacheDir);
        _delTimer = _time.CreateTimer(
            _ => DrainTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Сменить папку кэша на лету: новые чтения пойдут в неё. Уже скачанные файлы
    // остаются в прежней папке (не переносятся) — повторное чтение их перекачает.
    public void SetCacheDir(string path)
    {
        Directory.CreateDirectory(path);
        Volatile.Write(ref _cacheDir, path);
        _downloads.Clear();
        _blockFetches.Clear();
    }

    public void ClearCache()
    {
        CancellationTokenSource oldCancellation;
        var cacheDir = Volatile.Read(ref _cacheDir);
        lock (_cacheEpochLock)
        {
            // Порядок блокировок — _cacheEpochLock → _listLock. Публикация и инвалидация
            // листинга берут только _listLock, поэтому старый ответ не переживёт смену аккаунта.
            lock (_listLock)
            {
                ++_accountEpoch;
                oldCancellation = _cacheEpochCancellation;
                _cacheEpochCancellation = new CancellationTokenSource();
                _listCache.Clear();
            }
            _tempUrls.Clear();
            _downloads.Clear();
            _blockFetches.Clear();
            _wholeMode.Clear();
        }

        oldCancellation.Cancel();
        oldCancellation.Dispose();
        ClearOwnedCacheFiles(cacheDir);
        lock (_storageLock)
        {
            _storage = null;
            _storageEpoch = Volatile.Read(ref _accountEpoch);
            _storageLoad = null;
            _storageLoadEpoch = _storageEpoch;
            _storageFailure = null;
            _storageRetryAfter = DateTime.MinValue;
        }
    }

    public GetUserStorageInfoResponse GetStorage()
    {
        Task<GetUserStorageInfoResponse> pending;
        TaskCompletionSource<GetUserStorageInfoResponse>? load = null;
        var epoch = Volatile.Read(ref _accountEpoch);
        var startedAt = _time.GetUtcNow().UtcDateTime;
        lock (_storageLock)
        {
            if (_storage is { } cached && _storageEpoch == epoch && startedAt - cached.At < StorageTtl)
                return cached.Info;

            if (startedAt < _storageRetryAfter)
            {
                if (_storage is { } stale && _storageEpoch == epoch)
                    return stale.Info;
                if (_storageEpoch == epoch)
                    _storageFailure?.Throw();
            }

            if (_storageLoad is { } existingLoad && _storageLoadEpoch == epoch)
                pending = existingLoad.Task;
            else
            {
                load = new TaskCompletionSource<GetUserStorageInfoResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _storageLoad = load;
                _storageLoadEpoch = epoch;
                pending = load.Task;
            }
        }

        if (load is null)
            return pending.GetAwaiter().GetResult();

        try
        {
            var info = _files.GetUserStorageInfo(new GetUserStorageInfoRequest(),
                deadline: DateTime.UtcNow.Add(StorageRpcTimeout));
            lock (_storageLock)
            {
                if (epoch != Volatile.Read(ref _accountEpoch))
                    throw new OperationCanceledException();
                _storage = (_time.GetUtcNow().UtcDateTime, info);
                _storageEpoch = epoch;
                _storageRetryAfter = DateTime.MinValue;
                _storageFailure = null;
                info = _storage.Value.Info;
            }

            load.TrySetResult(info);
            return info;
        }
        catch (Exception ex)
        {
            GetUserStorageInfoResponse? stale;
            lock (_storageLock)
            {
                stale = epoch == Volatile.Read(ref _accountEpoch) && _storageEpoch == epoch
                    ? _storage?.Info
                    : null;
                if (epoch == Volatile.Read(ref _accountEpoch))
                {
                    _storageRetryAfter = _time.GetUtcNow().UtcDateTime + StorageFailureCache;
                    _storageFailure = ExceptionDispatchInfo.Capture(ex);
                }
            }

            if (stale is not null)
            {
                load.TrySetResult(stale);
                return stale;
            }

            load.TrySetException(ex);
            return pending.GetAwaiter().GetResult();
        }
        finally
        {
            lock (_storageLock)
            {
                if (ReferenceEquals(_storageLoad, load))
                    _storageLoad = null;
            }
        }
    }

    public DirectoryListingDetailed ListDirectory(string dirId, CancellationToken cancellationToken = default)
        => HidePending(ListDirectoryUnfiltered(dirId, cancellationToken));

    private DirectoryListingDetailed ListDirectoryUnfiltered(
        string dirId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListingLoadKey key;
            TaskCompletionSource<DirectoryListingDetailed> load;
            DirectoryListingDetailed? cachedListing = null;
            var ownsLoad = false;
            lock (_listLock)
            {
                _listGen.TryGetValue(dirId, out var generation);
                var accountEpoch = Volatile.Read(ref _accountEpoch);
                key = new ListingLoadKey(dirId, generation, accountEpoch);

                if (_listCache.TryGetValue(dirId, out var cached)
                    && cached.Generation == generation
                    && cached.AccountEpoch == accountEpoch
                    && _time.GetUtcNow().UtcDateTime - cached.At < ListTtl)
                    cachedListing = cached.Listing;

                if (cachedListing is null)
                {
                    if (!_listLoads.TryGetValue(key, out load!))
                    {
                        load = new TaskCompletionSource<DirectoryListingDetailed>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        _listLoads.Add(key, load);
                        ownsLoad = true;
                    }
                }
                else
                    load = null!;
            }

            if (cachedListing is not null)
                return cachedListing;

            if (!ownsLoad)
                ListingLoadWaiterJoinedForTesting?.Invoke();

            if (ownsLoad)
            {
                DirectoryListingDetailed? loadedListing = null;
                OperationCanceledException? cancellation = null;
                Exception? failure = null;
                try
                {
                    var listing = LoadDirectoryListing(dirId, cancellationToken);
                    loadedListing = listing;
                    var cached = new CachedDirectoryListing(
                        _time.GetUtcNow().UtcDateTime,
                        key.Generation,
                        key.AccountEpoch,
                        listing,
                        CreateDirectoryListingIndex(listing));

                    // Кэшируем только если за время RPC папку не инвалидировали: иначе ответ устарел
                    // (например, запись удалена и тумбстон уже снят) и не должен затирать инвалидацию,
                    // удерживая исчезнувший файл в кэше на ListTtl. Проверка поколения и запись кэша —
                    // под _listLock (тем же, что инвалидация), иначе попавшая между ними InvalidateListing терялась бы.
                    lock (_listLock)
                    {
                        _listGen.TryGetValue(dirId, out var generationAfter);
                        if (key.Generation == generationAfter
                            && key.AccountEpoch == Volatile.Read(ref _accountEpoch))
                            _listCache[dirId] = cached;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    cancellation = ex;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    lock (_listLock)
                        if (_listLoads.TryGetValue(key, out var current) && ReferenceEquals(current, load))
                            _listLoads.Remove(key);
                }

                if (cancellation is not null)
                    load.TrySetCanceled(cancellation.CancellationToken);
                else if (failure is not null)
                    load.TrySetException(failure);
                else
                    load.TrySetResult(loadedListing!);
            }

            var result = ownsLoad ? load.Task : load.Task.WaitAsync(cancellationToken);
            try
            {
                return result.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!ownsLoad && !cancellationToken.IsCancellationRequested)
            {
                // У чужого токена владельца завершилась общая загрузка; повторно ищем кеш или новую загрузку.
            }
            catch (RpcException ex) when (!ownsLoad
                                          && ex.StatusCode == StatusCode.Cancelled
                                          && !cancellationToken.IsCancellationRequested)
            {
                // Отмена общей gRPC-загрузки не должна отменять живой вызов-ожидатель.
            }
        }
    }

    private DirectoryListingDetailed LoadDirectoryListing(string dirId, CancellationToken cancellationToken)
    {
        var listing = new DirectoryListingDetailed();
        var cursorName = "";
        var cursorEntryId = "";
        while (true)
        {
            var request = new ListDirectoryRequest
            {
                DirectoryId = dirId,
                Limit = DirectoryPageSize,
                CursorEntryId = cursorEntryId,
            };
            if (!string.IsNullOrEmpty(cursorName)) request.CursorName = cursorName;

            var page = _cloud.ListDirectoryDetailed(request, cancellationToken: cancellationToken);
            if (listing.Subdirs.Count == 0) listing.Subdirs.AddRange(page.Subdirs);
            listing.Files.AddRange(page.Files);

            if (string.IsNullOrEmpty(page.NextCursorName) || string.IsNullOrEmpty(page.NextCursorEntryId))
                break;

            cursorName = page.NextCursorName;
            cursorEntryId = page.NextCursorEntryId;
        }

        return listing;
    }

    private static DirectoryListingIndex CreateDirectoryListingIndex(DirectoryListingDetailed listing)
    {
        var subdirectoriesByName = new Dictionary<string, BarkCloud.Proto.Files.DirectoryInfo>(
            StringComparer.OrdinalIgnoreCase);
        var filesByName = new Dictionary<string, FileEntryDetailed>(StringComparer.OrdinalIgnoreCase);
        var subdirectoriesByExactName = new Dictionary<string, BarkCloud.Proto.Files.DirectoryInfo>(
            StringComparer.Ordinal);
        var filesByExactName = new Dictionary<string, FileEntryDetailed>(StringComparer.Ordinal);

        foreach (var directory in listing.Subdirs)
        {
            subdirectoriesByName.TryAdd(directory.Name, directory);
            subdirectoriesByExactName.TryAdd(directory.Name, directory);
        }
        foreach (var file in listing.Files)
        {
            filesByName.TryAdd(file.Entry.Name, file);
            filesByExactName.TryAdd(file.Entry.Name, file);
        }

        return new DirectoryListingIndex(
            subdirectoriesByName, subdirectoriesByExactName, filesByName, filesByExactName);
    }

    public FileEntryDetailed? FindFileEntryByFileId(string directoryId, string fileId,
        CancellationToken cancellationToken = default)
    {
        InvalidateListing(directoryId);
        return ListDirectory(directoryId, cancellationToken).Files.FirstOrDefault(file =>
            string.Equals(file.File.Id, fileId, StringComparison.Ordinal));
    }

    // Убирает из листинга записи, удаление которых ещё буферизовано (тумбстоны), не трогая кэш.
    private DirectoryListingDetailed HidePending(DirectoryListingDetailed listing)
    {
        if (_delTombstones.IsEmpty || !listing.Files.Any(f => _delTombstones.ContainsKey(f.Entry.Id)))
            return listing;

        var copy = new DirectoryListingDetailed();
        copy.Subdirs.AddRange(listing.Subdirs);
        copy.Files.AddRange(listing.Files.Where(f => !_delTombstones.ContainsKey(f.Entry.Id)));
        copy.NextCursorName = listing.NextCursorName;
        copy.NextCursorEntryId = listing.NextCursorEntryId;
        return copy;
    }

    public BarkCloud.Proto.Files.DirectoryInfo? FindSubdirectoryByName(
        string directoryId, DirectoryListingDetailed listing, string name, bool exactFirst = false)
    {
        var index = GetDirectoryListingIndex(directoryId, listing);
        if (index is not null)
        {
            if (!exactFirst)
                return index.SubdirectoriesByName.GetValueOrDefault(name);
            return index.SubdirectoriesByExactName.GetValueOrDefault(name)
                   ?? index.SubdirectoriesByName.GetValueOrDefault(name);
        }

        var exact = exactFirst
            ? listing.Subdirs.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal))
            : null;
        return exact ?? listing.Subdirs.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public FileEntryDetailed? FindFileByName(
        string directoryId, DirectoryListingDetailed listing, string name, bool exactFirst = false)
    {
        var index = GetDirectoryListingIndex(directoryId, listing);
        if (index is not null)
        {
            if (!exactFirst)
            {
                var indexed = index.FilesByName.GetValueOrDefault(name);
                if (indexed is null || !_delTombstones.ContainsKey(indexed.Entry.Id))
                    return indexed;
            }
            else
            {
                var indexedExact = index.FilesByExactName.GetValueOrDefault(name);
                if (indexedExact is not null && !_delTombstones.ContainsKey(indexedExact.Entry.Id))
                    return indexedExact;
                if (indexedExact is null)
                {
                    var indexed = index.FilesByName.GetValueOrDefault(name);
                    if (indexed is null || !_delTombstones.ContainsKey(indexed.Entry.Id))
                        return indexed;
                }
            }
        }

        var exact = exactFirst
            ? listing.Files.FirstOrDefault(item =>
                string.Equals(item.Entry.Name, name, StringComparison.Ordinal)
                && !_delTombstones.ContainsKey(item.Entry.Id))
            : null;
        return exact ?? listing.Files.FirstOrDefault(item =>
            string.Equals(item.Entry.Name, name, StringComparison.OrdinalIgnoreCase)
            && !_delTombstones.ContainsKey(item.Entry.Id));
    }

    private DirectoryListingIndex? GetDirectoryListingIndex(string directoryId, DirectoryListingDetailed listing)
        => _listCache.TryGetValue(directoryId, out var cached)
           && ReferenceEquals(cached.Listing, listing)
            ? cached.Index
            : null;

    public bool HasTombstonedFile(string directoryId, string name)
    {
        ListDirectory(directoryId);
        return _listCache.TryGetValue(directoryId, out var cached)
               && cached.Listing.Files.Any(file =>
                   string.Equals(file.Entry.Name, name, StringComparison.OrdinalIgnoreCase)
                   && _delTombstones.ContainsKey(file.Entry.Id));
    }

    public void InvalidateListing(string dirId)
    {
        lock (_listLock)
        {
            _listCache.TryRemove(dirId, out _);
            _listGen.AddOrUpdate(dirId, 1, (_, g) => g + 1); // бампаем поколение — текущие in-flight листинги этой папки не закэшируются
        }
    }

    public ResolvedNode? Resolve(string path)
    {
        path = path.Replace('/', '\\');
        if (string.IsNullOrEmpty(path) || path == "\\")
            return new ResolvedNode { IsDirectory = true, DirectoryId = "", Name = "\\" };

        var segments = path.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var currentDir = "";

        for (var i = 0; i < segments.Length; i++)
        {
            var listing = ListDirectoryUnfiltered(currentDir);
            var last = i == segments.Length - 1;
            var seg = segments[i];

            var sub = FindSubdirectoryByName(currentDir, listing, seg);
            if (sub != null)
            {
                if (last)
                    return new ResolvedNode
                    {
                        IsDirectory = true,
                        DirectoryId = sub.Id,
                        Name = sub.Name,
                        Created = ToDate(sub.CreatedAt),
                        Updated = ToDate(sub.UpdatedAt),
                    };

                currentDir = sub.Id;
                continue;
            }

            if (last)
            {
                var fe = FindFileByName(currentDir, listing, seg);
                if (fe != null)
                    return new ResolvedNode
                    {
                        IsDirectory = false,
                        DirectoryId = fe.Entry.DirectoryId, // родительская папка
                        EntryId = fe.Entry.Id,
                        FileId = fe.File.Id,
                        Name = fe.Entry.Name,
                        Length = fe.File.FileSize,
                        Created = ToDate(fe.Entry.CreatedAt),
                        Updated = ToDate(fe.File.UploadedAt),
                    };
            }

            return null; // не найдено, либо файл в середине пути
        }

        return null;
    }

    // (id родительской папки, имя листа) для пути; null если родитель не папка.
    public (string DirectoryId, string Name)? ResolveParentDirectory(string path)
    {
        path = path.Replace('/', '\\').Trim('\\');
        var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var name = segments[^1];
        if (segments.Length == 1)
            return ("", name); // родитель — корень

        var parent = Resolve("\\" + string.Join('\\', segments[..^1]));
        if (parent is null || !parent.IsDirectory)
            return null;

        return (parent.DirectoryId, name);
    }

    // ───────── чтение содержимого (поблочно по HTTP Range; фаза 5) ─────────

    // Читает [offset, offset+len) файла, подкачивая недостающие блоки по 1 МиБ Range-запросами.
    // fileLength берётся из листинга (CloudFile.FileSize). Whole-режим включается только при 200 OK на Range-запрос.
    public int Read(string fileId, long fileLength, byte[] buffer, long offset,
        Func<IDisposable>? keepAliveFactory = null)
    {
        IDisposable? keepAlive = null;
        void StartKeepAlive()
        {
            keepAlive ??= keepAliveFactory?.Invoke();
        }

        try
        {
            var cacheDir = Volatile.Read(ref _cacheDir);
            var epoch = CaptureCacheEpoch();
            epoch.CancellationToken.ThrowIfCancellationRequested();
            if (_wholeMode.ContainsKey(fileId))
                return ReadWhole(fileId, fileLength, buffer, offset, cacheDir, StartKeepAlive,
                    epoch.Generation, epoch.CancellationToken);

            if (fileLength <= 0 || offset >= fileLength)
                return 0;

            try
            {
                return ReadBlocks(fileId, fileLength, buffer, offset, cacheDir, StartKeepAlive,
                    epoch.Generation, epoch.CancellationToken);
            }
            catch (RangeNotSupportedException)
            {
                lock (_cacheEpochLock)
                {
                    if (epoch.Generation != _accountEpoch)
                        throw new OperationCanceledException(epoch.CancellationToken);
                    _wholeMode[fileId] = true;
                }
                EngineLog.Info($"Сервер не отдал Range для {fileId} — переключаюсь на скачивание целиком");
                return ReadWhole(fileId, fileLength, buffer, offset, cacheDir, StartKeepAlive,
                    epoch.Generation, epoch.CancellationToken);
            }
        }
        finally
        {
            keepAlive?.Dispose();
        }
    }

    private int ReadBlocks(string fileId, long fileLength, byte[] buffer, long offset, string cacheDir,
        Action? startKeepAlive, long accountEpoch, CancellationToken epochToken)
    {
        var toRead = (int)Math.Min(buffer.Length, fileLength - offset);
        if (toRead <= 0)
            return 0;

        var endInclusive = offset + toRead - 1;
        var firstBlock = offset / BlockSize;
        var lastBlock = endInclusive / BlockSize;

        for (var b = firstBlock; b <= lastBlock; b++)
            EnsureBlock(cacheDir, fileId, fileLength, b, startKeepAlive, accountEpoch, epochToken);

        var bytesRead = 0;
        for (var b = firstBlock; b <= lastBlock; b++)
        {
            var blockStart = b * BlockSize;
            var blockEnd = Math.Min(blockStart + BlockSize, fileLength) - 1;
            var copyFrom = Math.Max(offset, blockStart);
            var copyTo = Math.Min(endInclusive, blockEnd);
            if (copyTo < copyFrom)
                continue;

            var len = (int)(copyTo - copyFrom + 1);
            var bufPos = (int)(copyFrom - offset);
            var inBlockPos = copyFrom - blockStart;

            using var handle = File.OpenHandle(BlockPath(cacheDir, fileId, b), FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = RandomAccess.Read(handle, buffer.AsSpan(bufPos, len), inBlockPos);
            bytesRead += read;
            if (read != len)
                break;
        }

        return bytesRead;
    }

    private void EnsureBlock(string cacheDir, string fileId, long fileLength, long blockIndex,
        Action? startKeepAlive, long accountEpoch, CancellationToken epochToken)
    {
        epochToken.ThrowIfCancellationRequested();
        var blockStart = blockIndex * BlockSize;
        var expectedLen = Math.Min(BlockSize, fileLength - blockStart);
        var path = BlockPath(cacheDir, fileId, blockIndex);
        if (HasExpectedLength(path, expectedLen))
            return;

        var key = new BlockFetchKey(cacheDir, fileId, fileLength, blockIndex, accountEpoch);
        var lazy = _blockFetches.GetOrAdd(key,
            _ => new Lazy<Task>(() => FetchBlockAsync(cacheDir, fileId, fileLength, blockIndex,
                accountEpoch, epochToken)));
        try
        {
            if (!lazy.IsValueCreated)
                startKeepAlive?.Invoke();
            var fetch = lazy.Value;
            if (!fetch.IsCompleted)
                startKeepAlive?.Invoke();
            fetch.GetAwaiter().GetResult();
        }
        finally
        {
            _blockFetches.TryRemove(new KeyValuePair<BlockFetchKey, Lazy<Task>>(key, lazy));
        }
    }

    private async Task FetchBlockAsync(string cacheDir, string fileId, long fileLength, long blockIndex,
        long accountEpoch, CancellationToken epochToken)
    {
        var start = blockIndex * BlockSize;
        var end = Math.Min(start + BlockSize, fileLength) - 1; // включительно
        var expectedLength = end - start + 1;
        var path = BlockPath(cacheDir, fileId, blockIndex);
        if (HasExpectedLength(path, expectedLength))
            return;

        using var rangeTimeout = new CancellationTokenSource(RangeBlockTimeout, _time);
        using var idleTimeout = new TransferIdleTimeout(_time);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            rangeTimeout.Token, idleTimeout.Token, epochToken);
        try
        {
            using var response = await SendDownloadRequestAsync(
                fileId, new RangeHeaderValue(start, end), cancellation.Token, accountEpoch);
            if (response.StatusCode == HttpStatusCode.OK)
                throw new RangeNotSupportedException();
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw CreateHttpException(response.StatusCode);

            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange is null
                || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                || contentRange.From != start
                || contentRange.To != end
                || contentRange.Length != fileLength)
                throw new InvalidDataException($"Сервер вернул неверный Content-Range для {fileId}");

            var directory = BlockDir(cacheDir, fileId);
            Directory.CreateDirectory(directory);
            var partPath = PartPath(path);
            try
            {
                await CopyToPartAsync(response, partPath, expectedLength, cancellation.Token, idleTimeout);
                if (!HasExpectedLength(path, expectedLength))
                {
                    try
                    {
                        lock (_cacheEpochLock)
                        {
                            if (accountEpoch != _accountEpoch)
                                throw new OperationCanceledException(epochToken);
                            File.Move(partPath, path, overwrite: true);
                        }
                    }
                    catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                               && HasExpectedLength(path, expectedLength))
                    {
                        // Другой запрос уже опубликовал готовый блок, возможно открытый читателем.
                    }
                }
            }
            finally
            {
                DeletePart(partPath);
            }
        }
        catch (OperationCanceledException ex) when (rangeTimeout.IsCancellationRequested || idleTimeout.Token.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("Eng_HttpTransferTimedOut"), ex);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled
                                      && (rangeTimeout.IsCancellationRequested || idleTimeout.Token.IsCancellationRequested))
        {
            throw new TimeoutException(Loc.T("Eng_HttpTransferTimedOut"), ex);
        }
    }

    private async Task<string> GetTempUrlAsync(string fileId, CancellationToken cancellationToken, long accountEpoch)
    {
        if (_tempUrls.TryGetValue(fileId, out var c) && c.Epoch == accountEpoch
            && _time.GetUtcNow().UtcDateTime - c.At < UrlTtl)
            return c.Url;

        var resp = await _files.GetTempDownloadUrlAsync(
            new GetTempDownloadUrlRequest { FileIds = { fileId } }, cancellationToken: cancellationToken);
        var raw = resp.FileUrls.FirstOrDefault()?.Url
                  ?? throw new InvalidOperationException($"Сервер не вернул download URL для {fileId}");
        var url = NormalizeDownloadUrl(raw);
        lock (_cacheEpochLock)
        {
            if (accountEpoch != _accountEpoch)
                throw new OperationCanceledException(cancellationToken);
            _tempUrls[fileId] = (accountEpoch, _time.GetUtcNow().UtcDateTime, url);
        }
        return url;
    }

    private static string BlockDir(string cacheDir, string fileId) => Path.Combine(cacheDir, fileId + ".blocks");
    private static string BlockPath(string cacheDir, string fileId, long blockIndex)
        => Path.Combine(BlockDir(cacheDir, fileId), blockIndex + ".blk");

    private CacheEpoch CaptureCacheEpoch()
    {
        lock (_cacheEpochLock)
            return new CacheEpoch(_accountEpoch, _cacheEpochCancellation.Token);
    }

    private static void ClearOwnedCacheFiles(string cacheDir)
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(cacheDir))
            {
                var name = Path.GetFileName(path);
                var isBlocks = name.EndsWith(".blocks", StringComparison.OrdinalIgnoreCase)
                               && Guid.TryParseExact(name[..^7], "D", out _);
                var isFile = Guid.TryParseExact(name, "D", out _);
                var separator = name.IndexOf('.');
                var isPart = separator > 0
                             && Guid.TryParseExact(name[..separator], "D", out _)
                             && name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
                if (!isBlocks && !isFile && !isPart)
                    continue;

                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                    else
                        File.Delete(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private readonly record struct CacheEpoch(long Generation, CancellationToken CancellationToken);

    // Скачивание файла целиком (whole-режим: сервер без Range, и для гидрации копии при записи).
    private int ReadWhole(string fileId, long fileLength, byte[] buffer, long offset, string cacheDir,
        Action? startKeepAlive, long accountEpoch, CancellationToken epochToken)
    {
        var cachedPath = Path.Combine(cacheDir, fileId);
        if (HasExpectedLength(cachedPath, fileLength))
            return ReadWholeFile(cachedPath, fileLength, buffer, offset);

        var key = new DownloadKey(cacheDir, fileId, fileLength, accountEpoch);
        var lazy = _downloads.GetOrAdd(key,
            _ => new Lazy<Task<string>>(() => DownloadAsync(cacheDir, fileId, fileLength,
                accountEpoch, epochToken)));
        string local;
        try
        {
            if (!lazy.IsValueCreated)
                startKeepAlive?.Invoke();
            var download = lazy.Value;
            if (!download.IsCompleted)
                startKeepAlive?.Invoke();
            local = download.GetAwaiter().GetResult();
        }
        finally
        {
            _downloads.TryRemove(new KeyValuePair<DownloadKey, Lazy<Task<string>>>(key, lazy));
        }

        return ReadWholeFile(local, fileLength, buffer, offset);
    }

    private static int ReadWholeFile(string local, long fileLength, byte[] buffer, long offset)
    {
        using var handle = File.OpenHandle(local, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset >= fileLength)
            return 0;

        var toRead = (int)Math.Min(buffer.Length, fileLength - offset);
        return RandomAccess.Read(handle, buffer.AsSpan(0, toRead), offset);
    }

    private sealed class RangeNotSupportedException : Exception;

    private readonly record struct DownloadKey(string CacheDir, string FileId, long FileLength, long AccountEpoch);
    private readonly record struct BlockFetchKey(
        string CacheDir, string FileId, long FileLength, long BlockIndex, long AccountEpoch);

    private async Task<string> DownloadAsync(string cacheDir, string fileId, long expectedLength,
        long accountEpoch, CancellationToken epochToken)
    {
        var local = Path.Combine(cacheDir, fileId);
        if (File.Exists(local))
        {
            if (new FileInfo(local).Length == expectedLength)
                return local;
            File.Delete(local);
        }

        var partPath = PartPath(local);
        try
        {
            await DownloadToPartAsync(fileId, partPath, expectedLength, epochToken, accountEpoch);
            lock (_cacheEpochLock)
            {
                if (accountEpoch != _accountEpoch)
                    throw new OperationCanceledException(epochToken);
                File.Move(partPath, local, overwrite: true);
            }
            return local;
        }
        finally
        {
            DeletePart(partPath);
        }
    }

    // Скачать блоб в произвольный путь (для гидрации рабочей копии при записи).
    public async Task DownloadToAsync(string fileId, string destPath, long expectedLength,
        CancellationToken cancellationToken = default)
    {
        var epoch = CaptureCacheEpoch();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, epoch.CancellationToken);
        var partPath = PartPath(destPath);
        try
        {
            await DownloadToPartAsync(fileId, partPath, expectedLength, linkedCancellation.Token, epoch.Generation);
            lock (_cacheEpochLock)
            {
                if (epoch.Generation != _accountEpoch)
                    throw new OperationCanceledException(epoch.CancellationToken);
                File.Move(partPath, destPath, overwrite: true);
            }
        }
        finally
        {
            DeletePart(partPath);
        }
    }

    private async Task DownloadToPartAsync(string fileId, string partPath, long expectedLength,
        CancellationToken cancellationToken = default, long? accountEpoch = null)
    {
        using var idleTimeout = new TransferIdleTimeout(_time);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, idleTimeout.Token);
        try
        {
            using var response = await SendDownloadRequestAsync(fileId, range: null, cancellation.Token,
                accountEpoch ?? Volatile.Read(ref _accountEpoch));
            if (response.StatusCode != HttpStatusCode.OK)
                throw CreateHttpException(response.StatusCode);

            await CopyToPartAsync(response, partPath, expectedLength, cancellation.Token, idleTimeout);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                                                   && idleTimeout.Token.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("Eng_HttpTransferTimedOut"), ex);
        }
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(string fileId, RangeHeaderValue? range,
        CancellationToken cancellationToken, long accountEpoch)
    {
        var retried = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = await GetTempUrlAsync(fileId, cancellationToken, accountEpoch);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (range is not null)
                request.Headers.Range = range;

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone))
                return response;

            var statusCode = response.StatusCode;
            response.Dispose();
            InvalidateTempUrl(fileId, url, accountEpoch);
            if (retried)
                throw CreateHttpException(statusCode);

            retried = true;
        }
    }

    private void InvalidateTempUrl(string fileId, string url, long accountEpoch)
    {
        if (accountEpoch == Volatile.Read(ref _accountEpoch)
            && _tempUrls.TryGetValue(fileId, out var cached)
            && cached.Epoch == accountEpoch && cached.Url == url)
            _tempUrls.TryRemove(new KeyValuePair<string, (long Epoch, DateTime At, string Url)>(fileId, cached));
    }

    private static HttpRequestException CreateHttpException(HttpStatusCode statusCode)
        => new($"Сервер вернул HTTP {(int)statusCode} ({statusCode}) при скачивании файла", null, statusCode);

    private static async Task CopyToPartAsync(HttpResponseMessage response, string partPath, long expectedLength,
        CancellationToken cancellationToken, TransferIdleTimeout idleTimeout)
    {
        await using var net = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await net.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            idleTimeout.ReportProgress();
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await file.FlushAsync(cancellationToken);
        var actualLength = new FileInfo(partPath).Length;
        if (actualLength != expectedLength)
            throw new InvalidDataException($"Ожидалось {expectedLength} байт, получено {actualLength}");
    }

    private static string PartPath(string path) => $"{path}.{Guid.NewGuid():N}.part";

    private static bool HasExpectedLength(string path, long expectedLength)
        => File.Exists(path) && new FileInfo(path).Length == expectedLength;

    private static void DeletePart(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EngineLog.Error($"Не удалось удалить временный файл {path}", ex);
        }
    }

    // ───────── запись ─────────

    public (string Url, string FileId) GetUploadUrl(CancellationToken cancellationToken = default)
    {
        var resp = _files.GetUploadUrl(new GetUploadUrlRequest { FileType = UploadFileType.CloudFile },
            cancellationToken: cancellationToken);
        return (resp.Url, resp.FileId);
    }

    // Заливает локальный файл (multipart-поле "file" + x-auth-token, как iOS).
    // Возвращает fileId из ответа; сервер не выполняет дедупликацию содержимого.
    public async Task<string> UploadAsync(string uploadUrl, string fileName, string localPath,
        CancellationToken cancellationToken = default)
    {
        using var idleTimeout = new TransferIdleTimeout(_time);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, idleTimeout.Token);
        using var content = new MultipartFormDataContent();
        // FileShare.ReadWrite: рабочая копия ещё открыта на запись хэндлом сессии.
        await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var progressStream = new ProgressStream(fileStream, idleTimeout.ReportProgress, idleTimeout.WaitForResponse);
        using var fileContent = new StreamContent(progressStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
        var token = _tokenProvider();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Add("x-auth-token", token);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
            idleTimeout.ReportProgress();
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: requestCancellation.Token);
            return doc.RootElement.GetProperty("fileId").GetString()
                   ?? throw new InvalidOperationException("Ответ загрузки без fileId");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                                                   && idleTimeout.Token.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("Eng_HttpTransferTimedOut"), ex);
        }
    }

    // Скачать байты аватара по его URL (тип UserAvatar качается напрямую по id, без temp-ссылки).
    // URL нормализуется на текущий Files-эндпоинт (как и download-ссылки — они могут быть устаревшими).
    public async Task<byte[]?> DownloadAvatarAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(NormalizeDownloadUrl(url));
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            EngineLog.Error("DownloadAvatar", ex);
            return null;
        }
    }

    public void AttachFile(string directoryId, string fileId, string name, CancellationToken cancellationToken = default)
        => _cloud.AttachFile(new AttachFileRequest { DirectoryId = directoryId, FileId = fileId, Name = name },
            cancellationToken: cancellationToken);

    public string CreateDirectory(string parentId, string name)
        => _cloud.CreateDirectory(new CreateDirectoryRequest { ParentId = parentId, Name = name }).Id;

    public void RenameFileEntry(string entryId, string newName)
        => _cloud.RenameFileEntry(new RenameFileEntryRequest { EntryId = entryId, NewName = newName });

    public void MoveFileEntry(string entryId, string newDirectoryId)
        => _cloud.MoveFileEntry(new MoveFileEntryRequest { EntryId = entryId, NewDirectoryId = newDirectoryId });

    public void DeleteFileEntry(string entryId, CancellationToken cancellationToken = default)
        => _cloud.DeleteFileEntry(new DeleteFileEntryRequest { EntryId = entryId },
            cancellationToken: cancellationToken);

    public void RestoreFromTrash(string entryId, string directoryId, CancellationToken cancellationToken = default)
    {
        _cloud.RestoreFromTrash(new RestoreFromTrashRequest { EntryId = entryId },
            cancellationToken: cancellationToken);
        InvalidateListing(directoryId);
        if (directoryId.Length > 0)
            InvalidateListing("");
    }

    // Буферизованное удаление файла: запись прячется из листингов сразу, а на сервер уходит
    // пачкой через DeleteFileEntries — по таймеру (1 c тишины) либо по достижении порога в 100.
    public void QueueDeleteFileEntry(string entryId, string parentDirId)
    {
        _delTombstones[entryId] = 0;     // скрыть из листингов немедленно
        InvalidateListing(parentDirId);  // следующее перечисление пересоберётся уже без записи

        bool atThreshold;
        lock (_delLock)
        {
            _delPending.Add((entryId, parentDirId));
            atThreshold = _delPending.Count >= DeleteBatchMax;
            if (!atThreshold)
                _delTimer.Change(DeleteWindow, Timeout.InfiniteTimeSpan); // (пере)запуск окна тишины
        }

        if (atThreshold)
            SendNow(allowRetry: true); // _delLock уже отпущен — SendNow возьмёт batch уже под _sendLock
    }

    // Колбэк таймера: фоновый дренаж по окну тишины. Неудачные пачки переотправляются (ретрай по таймеру).
    private void DrainTimer() => SendNow(allowRetry: true);

    // Синхронный дренаж до конца — для путей выхода (Unmount/Logout/Shutdown) и Dispose.
    // best-effort, без перевзвода таймера (на выходе ему уже некому сработать); drainAll
    // докручивает буфер до пустого, добирая всё, что фоновая попытка успела вернуть.
    // MoveFile передаёт throwOnFailure, чтобы не менять путь, если удаление цели не доставлено.
    public void FlushPending(bool throwOnFailure = false)
        => SendNow(allowRetry: false, drainAll: true, throwOnFailure: throwOnFailure);

    public void FlushPending(TimeSpan timeout, bool throwOnFailure = false, bool final = false)
    {
        if (final)
            Volatile.Write(ref _ending, 1);
        var elapsed = Stopwatch.StartNew();
        var deadline = _time.GetUtcNow().UtcDateTime.Add(timeout);
        if (!Monitor.TryEnter(_sendLock, timeout))
        {
            if (throwOnFailure)
                throw new TimeoutException(Loc.T("Eng_DeleteFlushTimeout"));

            DropQueuedDeletes();
            return;
        }

        try
        {
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                if (throwOnFailure)
                    throw new TimeoutException(Loc.T("Eng_DeleteFlushTimeout"));

                DropQueuedDeletes();
                return;
            }

            using var cancellation = new CancellationTokenSource(remaining);
            SendNow(allowRetry: false, drainAll: true, throwOnFailure: throwOnFailure,
                cancellationToken: cancellation.Token, totalDeadline: deadline);
        }
        finally
        {
            Monitor.Exit(_sendLock);
        }
    }

    internal void DropPendingDeletes()
    {
        lock (_sendLock)
            DropQueuedDeletes();
    }

    // Единая точка отправки. _sendLock берётся ПЕРЕД снятием batch из _delPending — это делает
    // «взять+отправить» атомарным: фоновый дренаж и блокирующий выход не накладываются и не
    // образуют окна, где batch уже снят, а _sendLock ещё свободен (иначе FlushPending мог бы
    // проскочить, не дождавшись in-flight отправки, и удаление потерялось бы).
    private void SendNow(bool allowRetry, bool drainAll = false, bool throwOnFailure = false,
        CancellationToken cancellationToken = default, DateTime? totalDeadline = null)
    {
        if (allowRetry && Volatile.Read(ref _ending) != 0)
            return;

        lock (_sendLock)
        {
            do
            {
                List<(string EntryId, string DirId)> batch;
                lock (_delLock)
                {
                    if (_delPending.Count == 0)
                        return;
                    batch = TakePendingLocked();
                }

                if (!SendChunks(batch, allowRetry, throwOnFailure, cancellationToken, totalDeadline))
                    return;
            }
            while (drainAll); // выход дренажит до пустого; фон — один проход (ретрай отложит таймер)
        }
    }

    // Забрать накопленное и остановить таймер — вызывается под _delLock.
    private List<(string EntryId, string DirId)> TakePendingLocked()
    {
        var copy = new List<(string EntryId, string DirId)>(_delPending);
        _delPending.Clear();
        _delTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return copy;
    }

    // Отправляет batch пачками по 100 с gRPC-дедлайном. Вызывается только под _sendLock.
    // При успехе снимает тумбстоны и инвалидирует листинги. При ошибке: allowRetry или throwOnFailure
    // → переотправка; иначе (выход) — сдаёмся сразу.
    private bool SendChunks(List<(string EntryId, string DirId)> batch, bool allowRetry, bool throwOnFailure,
        CancellationToken cancellationToken = default, DateTime? totalDeadline = null)
    {
        ExceptionDispatchInfo? firstFailure = null;
        var allSent = true;
        for (var i = 0; i < batch.Count; i += DeleteBatchMax)
        {
            var chunk = batch.GetRange(i, Math.Min(DeleteBatchMax, batch.Count - i));
            if (cancellationToken.IsCancellationRequested
                || totalDeadline is { } budget && _time.GetUtcNow().UtcDateTime >= budget)
            {
                allSent = false;
                var unsent = batch.GetRange(i, batch.Count - i);
                if (throwOnFailure || allowRetry && Volatile.Read(ref _ending) == 0)
                    RequeueUnsent(unsent);
                else
                    DropUnsent(unsent);
                if (throwOnFailure)
                    firstFailure ??= ExceptionDispatchInfo.Capture(new TimeoutException(Loc.T("Eng_DeleteFlushTimeout")));
                break;
            }

            var request = new DeleteFileEntriesRequest();
            request.EntryIds.AddRange(chunk.Select(x => x.EntryId));

            try
            {
                var deadline = _time.GetUtcNow().UtcDateTime.Add(DeleteDeadline);
                if (totalDeadline is { } overall && overall < deadline)
                    deadline = overall;
                _cloud.DeleteFileEntries(request, new CallOptions(deadline: deadline,
                    cancellationToken: cancellationToken));
            }
            catch (Exception ex)
            {
                EngineLog.Error($"Массовое удаление {chunk.Count} записей", ex);
                if (allowRetry && Volatile.Read(ref _ending) == 0 || throwOnFailure)
                {
                    RequeueFailed(chunk);
                    if (throwOnFailure)
                        firstFailure ??= ExceptionDispatchInfo.Capture(ex);
                }
                else
                    DropFailed(chunk); // на выходе ретраить некому — возвращаем файлы в листинг
                continue;
            }

            foreach (var (entryId, dirId) in chunk)
            {
                // Порядок важен: сначала сбрасываем кэш листинга, потом снимаем тумбстон —
                // иначе осталось бы микроокно, где запись из устаревшего кэша снова видна.
                InvalidateListing(dirId);
                _delTombstones.TryRemove(entryId, out _);
                _delAttempts.TryRemove(entryId, out _);
            }
        }

        firstFailure?.Throw();
        return allSent;
    }

    private void RequeueUnsent(List<(string EntryId, string DirId)> chunk)
    {
        if (chunk.Count == 0)
            return;

        lock (_delLock)
        {
            _delPending.AddRange(chunk);
            _delTimer.Change(DeleteWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void DropUnsent(List<(string EntryId, string DirId)> chunk)
    {
        foreach (var item in chunk)
            DropOne(item, attempts: null);
    }

    // Возвращает неотправленную пачку в очередь на повтор (тумбстоны держим — файлы остаются
    // скрытыми) и перевзводит таймер. Исчерпавшие лимит попыток сдаём через DropOne.
    private void RequeueFailed(List<(string EntryId, string DirId)> chunk)
    {
        var retry = new List<(string EntryId, string DirId)>();
        foreach (var item in chunk)
        {
            var attempts = _delAttempts.AddOrUpdate(item.EntryId, 1, (_, a) => a + 1);
            if (attempts < DeleteMaxAttempts)
                retry.Add(item);
            else
                DropOne(item, attempts);
        }

        if (retry.Count == 0)
            return;

        lock (_delLock)
        {
            _delPending.AddRange(retry);
            _delTimer.Change(DeleteWindow, Timeout.InfiniteTimeSpan);
        }
    }

    // Удаление не состоялось (путь выхода) — возвращаем файлы в листинг, снимая тумбстоны.
    private void DropFailed(List<(string EntryId, string DirId)> chunk)
    {
        foreach (var item in chunk)
            DropOne(item, _delAttempts.TryGetValue(item.EntryId, out var a) ? a + 1 : 1);
    }

    private void DropQueuedDeletes()
    {
        List<(string EntryId, string DirId)> queued;
        lock (_delLock)
            queued = TakePendingLocked();
        DropFailed(queued);
    }

    private void DropOne((string EntryId, string DirId) item, int? attempts)
    {
        var error = attempts is { } count
            ? Loc.T("Eng_DeleteFailedFmt", item.EntryId, count)
            : Loc.T("Eng_DeleteFlushTimeout");
        EngineLog.Info($"Удаление записи {item.EntryId} не выполнено — файл возвращён в листинг. {error}");
        try { SyncError?.Invoke(error); }
        catch (Exception ex) { EngineLog.Error("Обновление ошибки синхронизации удаления", ex); }
        InvalidateListing(item.DirId);
        _delTombstones.TryRemove(item.EntryId, out _);
        _delAttempts.TryRemove(item.EntryId, out _);
    }

    public void RenameDirectory(string directoryId, string newName)
        => _cloud.RenameDirectory(new RenameDirectoryRequest { DirectoryId = directoryId, NewName = newName });

    public void MoveDirectory(string directoryId, string newParentId)
        => _cloud.MoveDirectory(new MoveDirectoryRequest { DirectoryId = directoryId, NewParentId = newParentId });

    public void DeleteDirectory(string directoryId)
    {
        // Если после проверки содержимого появилась новая пачка, при сбое не удаляем папку рекурсивно.
        FlushPending(throwOnFailure: true);
        _cloud.DeleteDirectory(new DeleteDirectoryRequest { DirectoryId = directoryId });
    }

    // Пересобирает download-ссылку на актуальный Files-эндпоинт (как iOS normalizedFileDownloadURL).
    private string NormalizeDownloadUrl(string raw)
    {
        try
        {
            var parts = new Uri(raw).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.LastIndexOf(parts, "download");
            if (idx >= 0 && idx + 1 < parts.Length)
                return $"{_filesWebBase}/download/{parts[idx + 1]}";
        }
        catch { /* не похоже на ссылку скачивания — используем как есть */ }

        return raw;
    }

    private static DateTime? ToDate(Timestamp? t) => t?.ToDateTime();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ending, 1) == 0)
        {
            try { FlushPending(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { EngineLog.Error("Дренаж удалений при освобождении шлюза", ex); }
        }
        _delTimer.Dispose();
        _cacheEpochCancellation.Cancel();
        _cacheEpochCancellation.Dispose();
    }

    private sealed class TransferIdleTimeout : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ITimer _timer;

        public TransferIdleTimeout(TimeProvider time)
        {
            _timer = time.CreateTimer(_ => Cancel(), null, HttpTransferIdleLimit, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _cancellation.Token;

        public void ReportProgress() => _timer.Change(HttpTransferIdleLimit, Timeout.InfiniteTimeSpan);

        public void WaitForResponse() => _timer.Change(HttpUploadResponseLimit, Timeout.InfiniteTimeSpan);

        private void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _cancellation.Dispose();
        }
    }

    private sealed class ProgressStream(Stream stream, Action reportProgress, Action waitForResponse) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => stream.CanWrite;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = stream.Read(buffer, offset, count);
            if (read > 0)
                reportProgress();
            else
                waitForResponse();
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read > 0)
                reportProgress();
            else
                waitForResponse();
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            var read = await stream.ReadAsync(buffer, offset, count, cancellationToken);
            if (read > 0)
                reportProgress();
            else
                waitForResponse();
            return read;
        }

        public override void Flush() => stream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void SetLength(long value) => stream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                stream.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
