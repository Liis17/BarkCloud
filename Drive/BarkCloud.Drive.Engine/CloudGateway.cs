using System.Collections.Concurrent;

using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

namespace BarkCloud.Drive.Engine;

// Результат резолва пути "\A\b.txt" в облачные идентификаторы.
internal sealed class ResolvedNode
{
    public bool IsDirectory { get; init; }
    public string DirectoryId { get; init; } = "";  // для папок (её собственный id; "" = корень)
    public string FileId { get; init; } = "";        // для файлов (UploadFile.Id)
    public string Name { get; init; } = "";
    public long Length { get; init; }
    public DateTime? Created { get; init; }
    public DateTime? Updated { get; init; }
}

// Тонкая обёртка над CloudApi + FilesApi: листинги (с TTL-кэшем), резолв путей,
// и ленивое скачивание содержимого файла целиком в локальный кэш (Range — фаза 5).
internal sealed class CloudGateway : IDisposable
{
    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StorageTtl = TimeSpan.FromSeconds(10);

    private readonly CloudApi.CloudApiClient _cloud;
    private readonly FilesApi.FilesApiClient _files;
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly string _filesWebBase;

    private readonly ConcurrentDictionary<string, (DateTime At, DirectoryListingDetailed Listing)> _listCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _downloads = new();
    private readonly object _storageLock = new();
    private (DateTime At, GetUserStorageInfoResponse Info)? _storage;

    public CloudGateway(CloudApi.CloudApiClient cloud, FilesApi.FilesApiClient files, HttpClient http, string filesWebBase)
    {
        _cloud = cloud;
        _files = files;
        _http = http;
        _filesWebBase = filesWebBase.TrimEnd('/');
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

    // Читает диапазон из локально закэшированного файла (скачивая его целиком при первом обращении).
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

        var resp = _files.GetTempDownloadUrl(new GetTempDownloadUrlRequest { FileIds = { fileId } });
        var raw = resp.FileUrls.FirstOrDefault()?.Url
                  ?? throw new InvalidOperationException($"Сервер не вернул download URL для {fileId}");
        var url = NormalizeDownloadUrl(raw);

        var tmp = local + ".part";
        await using (var net = await _http.GetStreamAsync(url))
        await using (var file = File.Create(tmp))
            await net.CopyToAsync(file);

        File.Move(tmp, local, overwrite: true);
        return local;
    }

    // Пересобирает download-ссылку на актуальный Files-эндпоинт (как iOS normalizedFileDownloadURL):
    // сохранённые в БД URL могли быть сгенерированы при прежнем ExternalEndpoint:Host.
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
