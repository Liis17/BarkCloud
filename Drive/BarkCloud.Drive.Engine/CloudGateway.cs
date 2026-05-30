using System.Collections.Concurrent;
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
    private readonly string _cacheDir;
    private readonly string _filesWebBase;
    private readonly Func<string?> _tokenProvider;

    private readonly ConcurrentDictionary<string, (DateTime At, DirectoryListingDetailed Listing)> _listCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _downloads = new();
    private readonly object _storageLock = new();
    private (DateTime At, GetUserStorageInfoResponse Info)? _storage;

    public CloudGateway(CloudApi.CloudApiClient cloud, FilesApi.FilesApiClient files, HttpClient http,
        string filesWebBase, Func<string?> tokenProvider)
    {
        _cloud = cloud;
        _files = files;
        _http = http;
        _filesWebBase = filesWebBase.TrimEnd('/');
        _tokenProvider = tokenProvider;
        _cacheDir = Path.Combine(Path.GetTempPath(), "BarkCloudDrive");
        Directory.CreateDirectory(_cacheDir);
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

    // ───────── чтение содержимого (гидрация целиком; Range — фаза 5) ─────────

    public int Read(string fileId, byte[] buffer, long offset)
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
