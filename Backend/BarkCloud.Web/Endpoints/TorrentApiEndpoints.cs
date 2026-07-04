using System.Text.Json;

using BarkCloud.Proto.Torrent;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;

using Google.Protobuf;

using Grpc.Core;

namespace BarkCloud.Web.Endpoints;

/// <summary>
/// Same-origin JSON/SSE-API вкладки «Торренты». Проксирует в торрент-сервис
/// (TorrentApi) с пользовательским токеном из cookie. Живой прогресс — через SSE
/// поверх gRPC server-streaming StreamProgress.
/// </summary>
public static class TorrentApiEndpoints
{
    private static readonly JsonSerializerOptions Json = new();

    public static void MapTorrentApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/torrents");

        // Список торрентов пользователя.
        api.MapGet("", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await t.ListTorrentsAsync(new ListTorrentsRequest(), token);
                return Results.Json(new { torrents = resp.Torrents.Select(ToJson).ToArray() }, Json);
            }));

        // Добавить по magnet-ссылке.
        api.MapPost("/magnet", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t, MagnetReq body) =>
            await Guarded(http, auth, async token =>
            {
                if (string.IsNullOrWhiteSpace(body.Magnet))
                    return Results.Json(new { error = "Пустая ссылка" }, Json, statusCode: 400);

                var info = await t.AddMagnetAsync(new AddMagnetRequest { MagnetUri = body.Magnet.Trim() }, token);
                return Results.Json(ToJson(info), Json);
            }));

        // Добавить из .torrent-файла (multipart).
        api.MapPost("/file", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t) =>
            await Guarded(http, auth, async token =>
            {
                var form = await http.Request.ReadFormAsync();
                var file = form.Files["file"];
                if (file is null || file.Length == 0)
                    return Results.Json(new { error = "Файл не выбран" }, Json, statusCode: 400);

                await using var stream = file.OpenReadStream();
                var info = await t.AddTorrentFileAsync(new AddTorrentFileRequest
                {
                    TorrentFile = await ByteString.FromStreamAsync(stream)
                }, token);
                return Results.Json(ToJson(info), Json);
            })).DisableAntiforgery();

        // Файлы внутри торрента.
        api.MapGet("/{id}/files", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t, string id) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await t.ListFilesAsync(new TorrentIdRequest { Id = id }, token);
                return Results.Json(new { files = resp.Files.Select(ToFileJson).ToArray() }, Json);
            }));

        api.MapPost("/{id}/pause", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t, string id) =>
            await Guarded(http, auth, async token =>
            {
                await t.PauseTorrentAsync(new TorrentIdRequest { Id = id }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/{id}/resume", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t, string id) =>
            await Guarded(http, auth, async token =>
            {
                await t.ResumeTorrentAsync(new TorrentIdRequest { Id = id }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapDelete("/{id}", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t, string id, bool? deleteFiles) =>
            await Guarded(http, auth, async token =>
            {
                await t.RemoveTorrentAsync(new RemoveTorrentRequest { Id = id, DeleteFiles = deleteFiles ?? false }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/{id}/files/{index:int}/priority", async (HttpContext http, AuthGateway auth,
            TorrentApi.TorrentApiClient t, string id, int index, PriorityReq body) =>
            await Guarded(http, auth, async token =>
            {
                await t.SetFilePriorityAsync(new SetFilePriorityRequest
                {
                    Id = id,
                    FileIndex = index,
                    Priority = (TorrentFilePriority)body.Priority,
                }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // Импорт готового файла(ов) в облако.
        api.MapPost("/{id}/import", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t, string id, ImportReq body) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ImportToCloudRequest { Id = id, DirectoryId = body.Dir ?? "" };
                if (body.FileIndex.HasValue)
                    req.FileIndex = body.FileIndex.Value;

                var resp = await t.ImportToCloudAsync(req, token);
                return Results.Json(new { files = resp.Files.Select(f => new { fileId = f.FileId, name = f.Name }).ToArray() }, Json);
            }));

        // Скачивание/стриминг файла с диска (Range) — проксируем на HTTP1-эндпоинт торрент-сервиса.
        api.MapGet("/{id}/download", async (HttpContext http, AuthGateway auth,
            IHttpClientFactory httpFactory, IConfiguration config, string id, int? file) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null)
            {
                http.Response.StatusCode = 401;
                return;
            }

            var http1Base = config["TorrentService:Http1Base"];
            var url = $"{http1Base}/download/{id}?file={file ?? 0}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("x-auth-token", user.AccessToken);
            var range = http.Request.Headers.Range.ToString();
            if (!string.IsNullOrEmpty(range))
                request.Headers.TryAddWithoutValidation("Range", range);

            var client = httpFactory.CreateClient("torrent");
            using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);

            http.Response.StatusCode = (int)upstream.StatusCode;
            CopyHeader(upstream, http, "Content-Type");
            CopyHeader(upstream, http, "Content-Length");
            CopyHeader(upstream, http, "Content-Range");
            CopyHeader(upstream, http, "Accept-Ranges");
            CopyHeader(upstream, http, "Content-Disposition");

            await upstream.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        });

        // SSE: живой прогресс поверх gRPC server-streaming.
        api.MapGet("/stream", async (HttpContext http, AuthGateway auth, TorrentApi.TorrentApiClient t) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null)
            {
                http.Response.StatusCode = 401;
                return;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var token = BrowserContext.UserToken(user.AccessToken);
            using var call = t.StreamProgress(new StreamProgressRequest(), token, cancellationToken: http.RequestAborted);

            try
            {
                await foreach (var snapshot in call.ResponseStream.ReadAllAsync(http.RequestAborted))
                {
                    var payload = JsonSerializer.Serialize(
                        new { torrents = snapshot.Torrents.Select(ToJson).ToArray() }, Json);
                    await http.Response.WriteAsync($"data: {payload}\n\n", http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* клиент отключился */ }
            catch (RpcException) { /* стрим прерван */ }
        });
    }

    private static void CopyHeader(HttpResponseMessage upstream, HttpContext http, string name)
    {
        if (upstream.Content.Headers.TryGetValues(name, out var values)
            || upstream.Headers.TryGetValues(name, out values))
        {
            http.Response.Headers[name] = values.ToArray();
        }
    }

    private static object ToJson(TorrentInfo t) => new
    {
        id = t.Id,
        infoHash = t.InfoHash,
        name = t.Name,
        status = t.Status.ToString().Replace("TorrentStatus", "").ToLowerInvariant(),
        progress = t.Progress,
        totalSize = t.TotalSize,
        downloaded = t.Downloaded,
        uploaded = t.Uploaded,
        downloadSpeed = t.DownloadSpeed,
        uploadSpeed = t.UploadSpeed,
        seeds = t.Seeds,
        leechers = t.Leechers,
        ratio = t.Ratio,
        etaSeconds = t.EtaSeconds,
        completed = t.CompletedAt != null,
    };

    private static object ToFileJson(TorrentFileInfo f) => new
    {
        index = f.Index,
        path = f.Path,
        size = f.Size,
        downloaded = f.Downloaded,
        progress = f.Progress,
        priority = (int)f.Priority,
    };

    /// <summary>Авторизация по cookie + единая обработка gRPC-ошибок (как в CloudApiEndpoints).</summary>
    private static async Task<IResult> Guarded(HttpContext http, AuthGateway auth, Func<Metadata, Task<IResult>> action)
    {
        var user = await auth.AuthenticateAsync(http);
        if (user is null)
            return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);

        try
        {
            return await action(BrowserContext.UserToken(user.AccessToken));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            var code = ex.Trailers.GetValue("x-error-code");
            return Results.Json(new { error = ex.Status.Detail, code }, Json, statusCode: 400);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);
        }
        catch (RpcException ex)
        {
            return Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 502);
        }
    }

    private sealed record MagnetReq(string Magnet);
    private sealed record PriorityReq(int Priority);
    private sealed record ImportReq(string? Dir, int? FileIndex);
}
