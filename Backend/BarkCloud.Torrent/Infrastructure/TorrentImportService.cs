using BarkCloud.Proto.Files;

using Grpc.Core;

using MonoTorrent;
using MonoTorrent.Client;

namespace BarkCloud.Torrent.Infrastructure;

/// <summary>
/// Импорт скачанного файла в облако: переиспользует upload-путь Files
/// (GetUploadUrl → POST байтов на внутренний HTTP1-эндпоинт Files → AttachFile),
/// действуя от имени пользователя (проброс его JWT в метаданных).
/// </summary>
public class TorrentImportService
{
    private readonly FilesApi.FilesApiClient _files;
    private readonly CloudApi.CloudApiClient _cloud;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TorrentImportService> _logger;

    public TorrentImportService(
        FilesApi.FilesApiClient files,
        CloudApi.CloudApiClient cloud,
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<TorrentImportService> logger)
    {
        _files = files;
        _cloud = cloud;
        _httpFactory = httpFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public record ImportedFile(string FileId, string Name);

    /// <summary>Импортирует один завершённый файл торрента в облако.</summary>
    public async Task<ImportedFile?> ImportFileAsync(
        ITorrentManagerFile file, string directoryId, string userToken, CancellationToken ct)
    {
        var fullPath = file.FullPath;
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Файл для импорта не найден на диске: {Path}", fullPath);
            return null;
        }

        var headers = new Metadata { { "x-auth-token", userToken } };
        var name = Path.GetFileName(fullPath);

        // 1. Резервируем загрузку в Files.
        var upload = await _files.GetUploadUrlAsync(
            new GetUploadUrlRequest { FileType = UploadFileType.CloudFile }, headers, cancellationToken: ct);

        // 2. Заливаем байты на внутренний HTTP1-эндпоинт Files (минуя nginx).
        var http1Base = ResolveFilesHttp1Base();
        var client = _httpFactory.CreateClient("files-upload");

        await using var fileStream = File.OpenRead(fullPath);
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(fileStream);
        content.Add(streamContent, "file", name);

        using var resp = await client.PostAsync($"{http1Base}/upload/{upload.FileId}", content, ct);
        resp.EnsureSuccessStatusCode();

        // 3. Привязываем файл к папке облака (маршрутизация по типу медиа).
        await _cloud.AttachFileAsync(new AttachFileRequest
        {
            DirectoryId = directoryId,
            FileId = upload.FileId,
            Name = name,
            RouteByMediaKind = string.IsNullOrEmpty(directoryId),
        }, headers, cancellationToken: ct);

        _logger.LogInformation("Импортирован в облако: {Name} → {FileId}", name, upload.FileId);
        return new ImportedFile(upload.FileId, name);
    }

    private string ResolveFilesHttp1Base()
    {
        var host = new Uri(_configuration["FilesService:Host"]!).Host;
        var port = int.TryParse(Environment.GetEnvironmentVariable("FILES_HTTP1PORT"), out var p) && p > 0 ? p : 7026;
        return $"http://{host}:{port}";
    }
}
