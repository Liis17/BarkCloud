using System.Buffers;

namespace BarkCloud.Web.Infrastructure;

/// <summary>Ограниченная загрузка оригинала. URL поступает только из авторизованного gRPC-резолва.</summary>
public sealed class TextPreviewProxy(IHttpClientFactory httpFactory, IConfiguration config)
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public async Task<IResult> FetchAsync(HttpContext http, string downloadUrl)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Segments.Length < 3
            || uri.Segments[^2] != "download/"
            || !Guid.TryParse(uri.Segments[^1], out var tempId))
            return Error("Не удалось получить содержимое файла", 502);

        var http1Base = config["FilesService:Http1Base"];
        var fetchUrl = string.IsNullOrEmpty(http1Base) ? downloadUrl : $"{http1Base.TrimEnd('/')}/download/{tempId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
        using var upstream = await httpFactory.CreateClient("files-upload")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        if (!upstream.IsSuccessStatusCode)
            return Error("Содержимое файла недоступно", upstream.StatusCode == System.Net.HttpStatusCode.NotFound ? 404 : 502);
        if (upstream.Content.Headers.ContentLength > MaxBytes)
            return TooLarge();

        await using var stream = await upstream.Content.ReadAsStreamAsync(http.RequestAborted);
        using var bytes = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxBytes + 1L - bytes.Length)), http.RequestAborted);
                if (count == 0) break;
                if (bytes.Length + count > MaxBytes) return TooLarge();
                bytes.Write(buffer, 0, count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return Results.Bytes(bytes.ToArray(), "application/octet-stream");
    }

    private static IResult TooLarge() => Error("Файл больше 10 МиБ. Для просмотра скачайте его.", 413);
    private static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);
}
