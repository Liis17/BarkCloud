using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

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
    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StorageTtl = TimeSpan.FromSeconds(10);
    private const int DirectoryPageSize = 200;

    private readonly CloudApi.CloudApiClient _cloud;
    private readonly FilesApi.FilesApiClient _files;
    private readonly HttpClient _http;
    private string _cacheDir;
    private readonly string _filesWebBase;
    private readonly Func<string?> _tokenProvider;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, (DateTime At, DirectoryListingDetailed Listing)> _listCache = new();
    private readonly ConcurrentDictionary<string, int> _listGen = new(); // «поколение» инвалидации на папку — защита от записи устаревшего ответа
    private readonly object _listLock = new(); // делает (проверка поколения + запись кэша) и (инвалидация + бамп) атомарными друг к другу
    private readonly ConcurrentDictionary<DownloadKey, Lazy<Task<string>>> _downloads = new();
    private readonly object _storageLock = new();
    private (DateTime At, GetUserStorageInfoResponse Info)? _storage;

    // Поблочное чтение (фаза 5): кэш temp-URL на файл (живёт 60 мин — обновляем заранее),
    // дедуп параллельных скачиваний блоков, и файлы, для которых сервер не дал Range (целиком).
    private const int BlockSize = 1 << 20; // 1 МиБ
    private static readonly TimeSpan UrlTtl = TimeSpan.FromMinutes(50);
    private readonly ConcurrentDictionary<string, (DateTime At, string Url)> _tempUrls = new();
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
    private readonly List<(string EntryId, string DirId)> _delPending = new();
    private readonly ConcurrentDictionary<string, byte> _delTombstones = new();
    private readonly ConcurrentDictionary<string, int> _delAttempts = new();
    private readonly ITimer _delTimer;

    public Action<string>? SyncError { get; set; }

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

    public GetUserStorageInfoResponse GetStorage()
    {
        lock (_storageLock)
        {
            if (_storage is { } s && _time.GetUtcNow().UtcDateTime - s.At < StorageTtl)
                return s.Info;

            var info = _files.GetUserStorageInfo(new GetUserStorageInfoRequest());
            _storage = (_time.GetUtcNow().UtcDateTime, info);
            return info;
        }
    }

    public DirectoryListingDetailed ListDirectory(string dirId)
    {
        if (_listCache.TryGetValue(dirId, out var c) && _time.GetUtcNow().UtcDateTime - c.At < ListTtl)
            return HidePending(c.Listing);

        _listGen.TryGetValue(dirId, out var gen);
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

            var page = _cloud.ListDirectoryDetailed(request);
            if (listing.Subdirs.Count == 0) listing.Subdirs.AddRange(page.Subdirs);
            listing.Files.AddRange(page.Files);

            if (string.IsNullOrEmpty(page.NextCursorName) || string.IsNullOrEmpty(page.NextCursorEntryId))
                break;

            cursorName = page.NextCursorName;
            cursorEntryId = page.NextCursorEntryId;
        }

        // Кэшируем только если за время RPC папку не инвалидировали: иначе ответ устарел
        // (например, запись удалена и тумбстон уже снят) и не должен затирать инвалидацию,
        // удерживая исчезнувший файл в кэше на ListTtl. Проверка поколения и запись кэша —
        // под _listLock (тем же, что инвалидация), иначе попавшая между ними InvalidateListing терялась бы.
        lock (_listLock)
        {
            _listGen.TryGetValue(dirId, out var genAfter);
            if (gen == genAfter)
                _listCache[dirId] = (_time.GetUtcNow().UtcDateTime, listing);
        }

        return HidePending(listing);
    }

    public FileEntryDetailed? FindFileEntryByFileId(string directoryId, string fileId)
    {
        InvalidateListing(directoryId);
        return ListDirectory(directoryId).Files.FirstOrDefault(file =>
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
            var listing = ListDirectory(currentDir);
            var last = i == segments.Length - 1;
            var seg = segments[i];

            var sub = listing.Subdirs.FirstOrDefault(s => string.Equals(s.Name, seg, StringComparison.OrdinalIgnoreCase));
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
                var fe = listing.Files.FirstOrDefault(f =>
                    string.Equals(f.Entry.Name, seg, StringComparison.OrdinalIgnoreCase));
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
    public int Read(string fileId, long fileLength, byte[] buffer, long offset)
    {
        var cacheDir = Volatile.Read(ref _cacheDir);
        if (_wholeMode.ContainsKey(fileId))
            return ReadWhole(fileId, fileLength, buffer, offset, cacheDir);

        if (fileLength <= 0 || offset >= fileLength)
            return 0;

        try
        {
            return ReadBlocks(fileId, fileLength, buffer, offset, cacheDir);
        }
        catch (RangeNotSupportedException)
        {
            _wholeMode[fileId] = true;
            EngineLog.Info($"Сервер не отдал Range для {fileId} — переключаюсь на скачивание целиком");
            return ReadWhole(fileId, fileLength, buffer, offset, cacheDir);
        }
    }

    private int ReadBlocks(string fileId, long fileLength, byte[] buffer, long offset, string cacheDir)
    {
        var toRead = (int)Math.Min(buffer.Length, fileLength - offset);
        if (toRead <= 0)
            return 0;

        var endInclusive = offset + toRead - 1;
        var firstBlock = offset / BlockSize;
        var lastBlock = endInclusive / BlockSize;

        for (var b = firstBlock; b <= lastBlock; b++)
            EnsureBlock(cacheDir, fileId, fileLength, b);

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

    private void EnsureBlock(string cacheDir, string fileId, long fileLength, long blockIndex)
    {
        var blockStart = blockIndex * BlockSize;
        var expectedLen = Math.Min(BlockSize, fileLength - blockStart);
        var path = BlockPath(cacheDir, fileId, blockIndex);
        if (HasExpectedLength(path, expectedLen))
            return;

        var key = new BlockFetchKey(cacheDir, fileId, fileLength, blockIndex);
        var lazy = _blockFetches.GetOrAdd(key,
            _ => new Lazy<Task>(() => FetchBlockAsync(cacheDir, fileId, fileLength, blockIndex)));
        try
        {
            lazy.Value.GetAwaiter().GetResult();
        }
        finally
        {
            _blockFetches.TryRemove(new KeyValuePair<BlockFetchKey, Lazy<Task>>(key, lazy));
        }
    }

    private async Task FetchBlockAsync(string cacheDir, string fileId, long fileLength, long blockIndex)
    {
        var start = blockIndex * BlockSize;
        var end = Math.Min(start + BlockSize, fileLength) - 1; // включительно
        var expectedLength = end - start + 1;
        var path = BlockPath(cacheDir, fileId, blockIndex);
        if (HasExpectedLength(path, expectedLength))
            return;

        using var response = await SendDownloadRequestAsync(fileId, new RangeHeaderValue(start, end));
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
            await CopyToPartAsync(response, partPath, expectedLength);
            if (!HasExpectedLength(path, expectedLength))
            {
                try
                {
                    File.Move(partPath, path, overwrite: true);
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

    private async Task<string> GetTempUrlAsync(string fileId)
    {
        if (_tempUrls.TryGetValue(fileId, out var c) && _time.GetUtcNow().UtcDateTime - c.At < UrlTtl)
            return c.Url;

        var resp = await _files.GetTempDownloadUrlAsync(new GetTempDownloadUrlRequest { FileIds = { fileId } });
        var raw = resp.FileUrls.FirstOrDefault()?.Url
                  ?? throw new InvalidOperationException($"Сервер не вернул download URL для {fileId}");
        var url = NormalizeDownloadUrl(raw);
        _tempUrls[fileId] = (_time.GetUtcNow().UtcDateTime, url);
        return url;
    }

    private static string BlockDir(string cacheDir, string fileId) => Path.Combine(cacheDir, fileId + ".blocks");
    private static string BlockPath(string cacheDir, string fileId, long blockIndex)
        => Path.Combine(BlockDir(cacheDir, fileId), blockIndex + ".blk");

    // Скачивание файла целиком (whole-режим: сервер без Range, и для гидрации копии при записи).
    private int ReadWhole(string fileId, long fileLength, byte[] buffer, long offset, string cacheDir)
    {
        var key = new DownloadKey(cacheDir, fileId, fileLength);
        var lazy = _downloads.GetOrAdd(key,
            _ => new Lazy<Task<string>>(() => DownloadAsync(cacheDir, fileId, fileLength)));
        string local;
        try
        {
            local = lazy.Value.GetAwaiter().GetResult();
        }
        finally
        {
            _downloads.TryRemove(new KeyValuePair<DownloadKey, Lazy<Task<string>>>(key, lazy));
        }

        using var handle = File.OpenHandle(local, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset >= fileLength)
            return 0;

        var toRead = (int)Math.Min(buffer.Length, fileLength - offset);
        return RandomAccess.Read(handle, buffer.AsSpan(0, toRead), offset);
    }

    private sealed class RangeNotSupportedException : Exception;

    private readonly record struct DownloadKey(string CacheDir, string FileId, long FileLength);
    private readonly record struct BlockFetchKey(string CacheDir, string FileId, long FileLength, long BlockIndex);

    private async Task<string> DownloadAsync(string cacheDir, string fileId, long expectedLength)
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
            await DownloadToPartAsync(fileId, partPath, expectedLength);
            File.Move(partPath, local, overwrite: true);
            return local;
        }
        finally
        {
            DeletePart(partPath);
        }
    }

    // Скачать блоб в произвольный путь (для гидрации рабочей копии при записи).
    public async Task DownloadToAsync(string fileId, string destPath, long expectedLength)
    {
        var partPath = PartPath(destPath);
        try
        {
            await DownloadToPartAsync(fileId, partPath, expectedLength);
            File.Move(partPath, destPath, overwrite: true);
        }
        finally
        {
            DeletePart(partPath);
        }
    }

    private async Task DownloadToPartAsync(string fileId, string partPath, long expectedLength)
    {
        using var response = await SendDownloadRequestAsync(fileId, range: null);
        if (response.StatusCode != HttpStatusCode.OK)
            throw CreateHttpException(response.StatusCode);

        await CopyToPartAsync(response, partPath, expectedLength);
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(string fileId, RangeHeaderValue? range)
    {
        var retried = false;
        while (true)
        {
            var url = await GetTempUrlAsync(fileId);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (range is not null)
                request.Headers.Range = range;

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone))
                return response;

            var statusCode = response.StatusCode;
            response.Dispose();
            InvalidateTempUrl(fileId, url);
            if (retried)
                throw CreateHttpException(statusCode);

            retried = true;
        }
    }

    private void InvalidateTempUrl(string fileId, string url)
    {
        if (_tempUrls.TryGetValue(fileId, out var cached) && cached.Url == url)
            _tempUrls.TryRemove(new KeyValuePair<string, (DateTime At, string Url)>(fileId, cached));
    }

    private static HttpRequestException CreateHttpException(HttpStatusCode statusCode)
        => new($"Сервер вернул HTTP {(int)statusCode} ({statusCode}) при скачивании файла", null, statusCode);

    private static async Task CopyToPartAsync(HttpResponseMessage response, string partPath, long expectedLength)
    {
        await using (var net = await response.Content.ReadAsStreamAsync())
        await using (var file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         81920, FileOptions.Asynchronous))
            await net.CopyToAsync(file);

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

    public (string Url, string FileId) GetUploadUrl()
    {
        var resp = _files.GetUploadUrl(new GetUploadUrlRequest { FileType = UploadFileType.CloudFile });
        return (resp.Url, resp.FileId);
    }

    // Заливает локальный файл (multipart-поле "file" + x-auth-token, как iOS).
    // Возвращает fileId из ответа; сервер не выполняет дедупликацию содержимого.
    public async Task<string> UploadAsync(string uploadUrl, string fileName, string localPath)
    {
        using var content = new MultipartFormDataContent();
        // FileShare.ReadWrite: рабочая копия ещё открыта на запись хэндлом сессии.
        await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
        var token = _tokenProvider();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Add("x-auth-token", token);

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.GetProperty("fileId").GetString()
               ?? throw new InvalidOperationException("Ответ загрузки без fileId");
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

    public void AttachFile(string directoryId, string fileId, string name)
        => _cloud.AttachFile(new AttachFileRequest { DirectoryId = directoryId, FileId = fileId, Name = name });

    public string CreateDirectory(string parentId, string name)
        => _cloud.CreateDirectory(new CreateDirectoryRequest { ParentId = parentId, Name = name }).Id;

    public void RenameFileEntry(string entryId, string newName)
        => _cloud.RenameFileEntry(new RenameFileEntryRequest { EntryId = entryId, NewName = newName });

    public void MoveFileEntry(string entryId, string newDirectoryId)
        => _cloud.MoveFileEntry(new MoveFileEntryRequest { EntryId = entryId, NewDirectoryId = newDirectoryId });

    public void DeleteFileEntry(string entryId)
        => _cloud.DeleteFileEntry(new DeleteFileEntryRequest { EntryId = entryId });

    public void RestoreFromTrash(string entryId, string directoryId)
    {
        _cloud.RestoreFromTrash(new RestoreFromTrashRequest { EntryId = entryId });
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

    // Единая точка отправки. _sendLock берётся ПЕРЕД снятием batch из _delPending — это делает
    // «взять+отправить» атомарным: фоновый дренаж и блокирующий выход не накладываются и не
    // образуют окна, где batch уже снят, а _sendLock ещё свободен (иначе FlushPending мог бы
    // проскочить, не дождавшись in-flight отправки, и удаление потерялось бы).
    private void SendNow(bool allowRetry, bool drainAll = false, bool throwOnFailure = false)
    {
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

                SendChunks(batch, allowRetry, throwOnFailure);
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
    private void SendChunks(List<(string EntryId, string DirId)> batch, bool allowRetry, bool throwOnFailure)
    {
        ExceptionDispatchInfo? firstFailure = null;
        for (var i = 0; i < batch.Count; i += DeleteBatchMax)
        {
            var chunk = batch.GetRange(i, Math.Min(DeleteBatchMax, batch.Count - i));
            var request = new DeleteFileEntriesRequest();
            request.EntryIds.AddRange(chunk.Select(x => x.EntryId));

            try
            {
                _cloud.DeleteFileEntries(request, deadline: _time.GetUtcNow().UtcDateTime.Add(DeleteDeadline));
            }
            catch (Exception ex)
            {
                EngineLog.Error($"Массовое удаление {chunk.Count} записей", ex);
                if (allowRetry || throwOnFailure)
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

    private void DropOne((string EntryId, string DirId) item, int attempts)
    {
        EngineLog.Info($"Удаление записи {item.EntryId} не выполнено (попыток: {attempts}) — файл возвращён в листинг");
        try { SyncError?.Invoke(Loc.T("Eng_DeleteFailedFmt", item.EntryId, attempts)); }
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
        FlushPending(); // не теряем буферизованные удаления при размонтировании/выходе
        _delTimer.Dispose();
        _http.Dispose();
    }
}
