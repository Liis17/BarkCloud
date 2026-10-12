using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

using BarkCloud.Proto.Files;

using Grpc.Core;

using MonoTorrent;
using MonoTorrent.Client;

namespace BarkCloud.Torrent.Infrastructure;

/// <summary>
/// Импорт скачанного файла в облако через Upload 2.0
/// (создание сессии → PUT частей → Complete → Ready → AttachFile),
/// действуя от имени пользователя (проброс его JWT в метаданных).
/// </summary>
public class TorrentImportService
{
    public const string HttpClientName = "files-upload";

    // = nginx proxy_read_timeout 7200s окна Web → Torrent ImportToCloud: дольше ответ до браузера не дойдёт.
    public static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(2);

    private readonly FilesApi.FilesApiClient _files;
    private readonly CloudApi.CloudApiClient _cloud;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TorrentImportService> _logger;
    private readonly TimeProvider _timeProvider;

    public TorrentImportService(
        FilesApi.FilesApiClient files,
        CloudApi.CloudApiClient cloud,
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<TorrentImportService> logger,
        TimeProvider? timeProvider = null)
    {
        _files = files;
        _cloud = cloud;
        _httpFactory = httpFactory;
        _configuration = configuration;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public record ImportedFile(string FileId, string Name);

    /// <summary>Клиент передачи байтов в Files: срок 2 ч вместо 100 с по умолчанию.</summary>
    public static IServiceCollection AddFilesUploadClient(IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, c => c.Timeout = TransferTimeout);
        return services;
    }

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

        using var timeout = new CancellationTokenSource(TransferTimeout, _timeProvider);
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var transferCt = transfer.Token;
        await using var fileStream = File.OpenRead(fullPath);
        var size = fileStream.Length;
        if (size == 0)
            throw new InvalidDataException($"Нельзя импортировать пустой файл: {name}.");

        string? sessionId = null;
        try
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fileStream, transferCt));
            fileStream.Position = 0;
            var session = await _files.CreateUploadSessionAsync(new CreateUploadSessionRequest
            {
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                FileName = name,
                FileSize = size,
                ContentType = "application/octet-stream",
                Sha256 = hash
            }, headers, cancellationToken: transferCt);
            sessionId = session.SessionId;
            if (session.Status != UploadSessionStatus.Uploading || session.PartSize <= 0
                || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(session.UploadToken))
                throw new InvalidDataException("Files вернул некорректную сессию импорта.");

            // Байты идут прямо в Files по внутреннему HTTP1, минуя nginx/TLS.
            var client = _httpFactory.CreateClient(HttpClientName);
            var http1Base = ResolveFilesHttp1Base();
            for (var offset = 0L; offset < size;)
            {
                var partNumber = checked((int)(offset / session.PartSize + 1));
                var length = Math.Min(session.PartSize, size - offset);
                using var content = new FilePartContent(fileStream, length);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, size);
                content.Headers.ContentLength = length;
                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"{http1Base}/file-upload/{Uri.EscapeDataString(sessionId)}/parts/{partNumber}") { Content = content };
                request.Headers.Add("X-Upload-Token", session.UploadToken);
                using var response = await client.SendAsync(request, transferCt);
                response.EnsureSuccessStatusCode();
                using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(transferCt));
                if (response.Content.Headers.ContentType?.MediaType != "application/json"
                    || !receipt.RootElement.TryGetProperty("partNumber", out var number)
                    || !number.TryGetInt32(out var confirmedNumber) || confirmedNumber != partNumber
                    || !receipt.RootElement.TryGetProperty("size", out var bytes)
                    || !bytes.TryGetInt64(out var confirmedSize) || confirmedSize != length)
                    throw new InvalidDataException($"Files не подтвердил часть {partNumber} импорта {name}.");
                offset += length;
            }

            var id = new UploadSessionIdRequest { SessionId = sessionId };
            session = await _files.CompleteUploadSessionAsync(id, headers, cancellationToken: transferCt);
            var delay = TimeSpan.FromSeconds(1);
            while (session.Status == UploadSessionStatus.Processing)
            {
                await Task.Delay(delay, transferCt);
                session = await _files.GetUploadSessionAsync(id, headers, cancellationToken: transferCt);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
            if (session.Status != UploadSessionStatus.Ready || string.IsNullOrEmpty(session.FileId))
                throw new InvalidDataException($"Не удалось обработать импорт {name}: {session.Status}, {session.ErrorCode} {session.ErrorMessage}");

            await _cloud.AttachFileAsync(new AttachFileRequest
            {
                DirectoryId = directoryId,
                FileId = session.FileId,
                Name = name,
                RouteByMediaKind = string.IsNullOrEmpty(directoryId),
                UploadSessionId = sessionId
            }, headers, cancellationToken: transferCt);

            _logger.LogInformation("Импортирован в облако: {Name} → {FileId}", name, session.FileId);
            return new ImportedFile(session.FileId, name);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                // Отмена пользователя не должна отменять попытку освобождения резерва.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await _files.CancelUploadSessionAsync(new UploadSessionIdRequest { SessionId = sessionId },
                        headers, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cleanup.Token);
                }
                catch (Exception cancelError)
                {
                    _logger.LogWarning(cancelError, "Не удалось отменить сессию импорта {SessionId}", sessionId);
                }
            }
            if ((ex is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled })
                && !ct.IsCancellationRequested)
                throw new TimeoutException($"Превышено время импорта файла {name} в облако.", ex);
            throw;
        }
    }

    private string ResolveFilesHttp1Base()
    {
        var host = new Uri(_configuration["FilesService:Host"]!).Host;
        var port = int.TryParse(Environment.GetEnvironmentVariable("FILES_HTTP1PORT"), out var p) && p > 0 ? p : 7026;
        return $"http://{host}:{port}";
    }

    // Ограничиваем чтение одной частью без буферизации всего файла в памяти.
    private sealed class FilePartContent(Stream source, long length) : HttpContent
    {
        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var buffer = new byte[81920];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0)
                    throw new EndOfStreamException("Файл торрента закончился до завершения части.");
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }
        }
    }
}
