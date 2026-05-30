using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

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

    private readonly CloudApi.CloudApiClient _cloud;
    private readonly FilesApi.FilesApiClient _files;
    private readonly HttpClient _http;
    private string _cacheDir;
    private readonly string _filesWebBase;
    private readonly Func<string?> _tokenProvider;

    private readonly ConcurrentDictionary<string, (DateTime At, DirectoryListingDetailed Listing)> _listCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _downloads = new();
    private readonly object _storageLock = new();
    private (DateTime At, GetUserStorageInfoResponse Info)? _storage;

    // Поблочное чтение (фаза 5): кэш temp-URL на файл (живёт 60 мин — обновляем заранее),
    // дедуп параллельных скачиваний блоков, и файлы, для которых сервер не дал Range (целиком).
    private const int BlockSize = 1 << 20; // 1 МиБ
    private static readonly TimeSpan UrlTtl = TimeSpan.FromMinutes(50);
    private readonly ConcurrentDictionary<string, (DateTime At, string Url)> _tempUrls = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _blockFetches = new();
    private readonly ConcurrentDictionary<string, bool> _wholeMode = new();

    public CloudGateway(CloudApi.CloudApiClient cloud, FilesApi.FilesApiClient files, HttpClient http,
        string filesWebBase, Func<string?> tokenProvider, string cacheDir)
    {
        _cloud = cloud;
        _files = files;
        _http = http;
        _filesWebBase = filesWebBase.TrimEnd('/');
        _tokenProvider = tokenProvider;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    // Сменить папку кэша на лету: новые чтения пойдут в неё. Уже скачанные файлы
    // остаются в прежней папке (не переносятся) — повторное чтение их перекачает.
    public void SetCacheDir(string path)
    {
        Directory.CreateDirectory(path);
        _cacheDir = path;
        _downloads.Clear();
    }

    public GetUserStorageInfoResponse GetStorage()
    {
        lock (_storageLock)
        {
            if (_storage is { } s && DateTime.UtcNow - s.At < StorageTtl)
                return s.Info;

            var info = _files.GetUserStorageInfo(new GetUserStorageInfoRequest());
            _storage = (DateTime.UtcNow, info);
            return info;
        }
    }

    public DirectoryListingDetailed ListDirectory(string dirId)
    {
        if (_listCache.TryGetValue(dirId, out var c) && DateTime.UtcNow - c.At < ListTtl)
            return c.Listing;

        var listing = _cloud.ListDirectoryDetailed(new ListDirectoryRequest { DirectoryId = dirId });
        _listCache[dirId] = (DateTime.UtcNow, listing);
        return listing;
    }

    public void InvalidateListing(string dirId) => _listCache.TryRemove(dirId, out _);

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
    // fileLength берётся из листинга (CloudFile.FileSize). Если сервер не поддерживает Range —
    // откатываемся на скачивание файла целиком (whole-режим).
    public int Read(string fileId, long fileLength, byte[] buffer, long offset)
    {
        if (_wholeMode.ContainsKey(fileId))
            return ReadWhole(fileId, buffer, offset);

        if (fileLength <= 0 || offset >= fileLength)
            return 0;

        try
        {
            return ReadBlocks(fileId, fileLength, buffer, offset);
        }
        catch (RangeNotSupportedException)
        {
            _wholeMode[fileId] = true;
            EngineLog.Info($"Сервер не отдал Range для {fileId} — переключаюсь на скачивание целиком");
            return ReadWhole(fileId, buffer, offset);
        }
    }

    private int ReadBlocks(string fileId, long fileLength, byte[] buffer, long offset)
    {
        var toRead = (int)Math.Min(buffer.Length, fileLength - offset);
        if (toRead <= 0)
            return 0;

        var endInclusive = offset + toRead - 1;
        var firstBlock = offset / BlockSize;
        var lastBlock = endInclusive / BlockSize;

        for (var b = firstBlock; b <= lastBlock; b++)
            EnsureBlock(fileId, fileLength, b);

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

            using var handle = File.OpenHandle(BlockPath(fileId, b), FileMode.Open, FileAccess.Read, FileShare.Read);
            RandomAccess.Read(handle, buffer.AsSpan(bufPos, len), inBlockPos);
        }

        return toRead;
    }

    private void EnsureBlock(string fileId, long fileLength, long blockIndex)
    {
        var blockStart = blockIndex * BlockSize;
        var expectedLen = Math.Min(BlockSize, fileLength - blockStart);
        var path = BlockPath(fileId, blockIndex);
        if (File.Exists(path) && new FileInfo(path).Length == expectedLen)
            return;

        var key = $"{fileId}:{blockIndex}";
        var lazy = _blockFetches.GetOrAdd(key, _ => new Lazy<Task>(() => FetchBlockAsync(fileId, fileLength, blockIndex)));
        try
        {
            lazy.Value.GetAwaiter().GetResult();
        }
        finally
        {
            _blockFetches.TryRemove(key, out _);
        }
    }

    private async Task FetchBlockAsync(string fileId, long fileLength, long blockIndex)
    {
        var start = blockIndex * BlockSize;
        var end = Math.Min(start + BlockSize, fileLength) - 1; // включительно

        var url = await GetTempUrlAsync(fileId);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new RangeNotSupportedException(); // старый бэкенд без Range → откат на целиком

        Directory.CreateDirectory(BlockDir(fileId));
        var path = BlockPath(fileId, blockIndex);
        var tmp = path + ".part";

        await using (var net = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(tmp))
            await net.CopyToAsync(file);

        File.Move(tmp, path, overwrite: true);
    }

    private async Task<string> GetTempUrlAsync(string fileId)
    {
        if (_tempUrls.TryGetValue(fileId, out var c) && DateTime.UtcNow - c.At < UrlTtl)
            return c.Url;

        var resp = await _files.GetTempDownloadUrlAsync(new GetTempDownloadUrlRequest { FileIds = { fileId } });
        var raw = resp.FileUrls.FirstOrDefault()?.Url
                  ?? throw new InvalidOperationException($"Сервер не вернул download URL для {fileId}");
        var url = NormalizeDownloadUrl(raw);
        _tempUrls[fileId] = (DateTime.UtcNow, url);
        return url;
    }

    private string BlockDir(string fileId) => Path.Combine(_cacheDir, fileId + ".blocks");
    private string BlockPath(string fileId, long blockIndex) => Path.Combine(BlockDir(fileId), blockIndex + ".blk");

    // Скачивание файла целиком (whole-режим: сервер без Range, и для гидрации копии при записи).
    private int ReadWhole(string fileId, byte[] buffer, long offset)
    {
        var local = _downloads
            .GetOrAdd(fileId, id => new Lazy<Task<string>>(() => DownloadAsync(id)))
            .Value.GetAwaiter().GetResult();

        using var handle = File.OpenHandle(local, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = RandomAccess.GetLength(handle);
        if (offset >= length)
            return 0;

        var toRead = (int)Math.Min(buffer.Length, length - offset);
        return RandomAccess.Read(handle, buffer.AsSpan(0, toRead), offset);
    }

    private sealed class RangeNotSupportedException : Exception;

    private async Task<string> DownloadAsync(string fileId)
    {
        var local = Path.Combine(_cacheDir, fileId);
        if (File.Exists(local))
            return local;

        var tmp = local + ".part";
        await DownloadToAsync(fileId, tmp);
        File.Move(tmp, local, overwrite: true);
        return local;
    }

    // Скачать блоб в произвольный путь (для гидрации рабочей копии при записи).
    public async Task DownloadToAsync(string fileId, string destPath)
    {
        var resp = _files.GetTempDownloadUrl(new GetTempDownloadUrlRequest { FileIds = { fileId } });
        var raw = resp.FileUrls.FirstOrDefault()?.Url
                  ?? throw new InvalidOperationException($"Сервер не вернул download URL для {fileId}");

        await using var net = await _http.GetStreamAsync(NormalizeDownloadUrl(raw));
        await using var file = File.Create(destPath);
        await net.CopyToAsync(file);
    }

    // ───────── запись ─────────

    public (string Url, string FileId) GetUploadUrl()
    {
        var resp = _files.GetUploadUrl(new GetUploadUrlRequest { FileType = UploadFileType.CloudFile });
        return (resp.Url, resp.FileId);
    }

    // Заливает локальный файл (multipart-поле "file" + x-auth-token, как iOS).
    // Возвращает эффективный fileId из ответа (после серверной дедупликации).
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

    public void RenameDirectory(string directoryId, string newName)
        => _cloud.RenameDirectory(new RenameDirectoryRequest { DirectoryId = directoryId, NewName = newName });

    public void MoveDirectory(string directoryId, string newParentId)
        => _cloud.MoveDirectory(new MoveDirectoryRequest { DirectoryId = directoryId, NewParentId = newParentId });

    public void DeleteDirectory(string directoryId)
        => _cloud.DeleteDirectory(new DeleteDirectoryRequest { DirectoryId = directoryId });

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

    public void Dispose() => _http.Dispose();
}
