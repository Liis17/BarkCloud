using BarkCloud.GrpcServer;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Grpc.Core;

using System.Net;
using System.Text.Json;

namespace BarkCloud.Web;

public static class WebEndpoints
{
    public static void MapWebEndpoints(this WebApplication app)
    {
        // Корень: на главную (Фото) если авторизован, иначе на логин
        app.MapGet("/", async (HttpContext http, AuthGateway auth) =>
            await auth.AuthenticateAsync(http) is not null
                ? Results.Redirect("/photos")
                : Results.Redirect("/login"));

        // Same-origin прокси для favicon-превью: браузерный canvas не может надёжно
        // скруглить внешнюю картинку без CORS, поэтому отдаём разрешённые preview URL через Web.
        app.MapGet("/api/head/icon", async (HttpContext http, IHttpClientFactory httpFactory, IConfiguration config, string? url) =>
        {
            if (!TryValidateIconProxyUrl(http, config, url, out var target))
                return Results.BadRequest();

            try
            {
                var client = httpFactory.CreateClient("files-upload");
                using var resp = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                    return Results.StatusCode(StatusCodes.Status502BadGateway);

                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

                var bytes = await resp.Content.ReadAsByteArrayAsync(http.RequestAborted);
                if (bytes.Length > 2_097_152)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                http.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.File(bytes, contentType);
            }
            catch
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        });

        // Same-origin прокси байтов картинки для кнопки «копировать в буфер» во вьюверах.
        // Браузер не даёт прочитать пиксели картинки чужого origin (tainted canvas / CORS),
        // поэтому отдаём содержимое через web. Анонимный (нужен и публичным страницам шаринга).
        // SSRF не возможен: из входного URL берётся только GUID блоба, запрос всегда идёт на
        // внутренний Files (/download/{id}), а тот отдаёт лишь превью-блобы и temp-файлы.
        app.MapGet("/api/files/image", async (HttpContext http, IHttpClientFactory httpFactory, IConfiguration config, string? url) =>
        {
            if (string.IsNullOrEmpty(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !uri.AbsolutePath.Contains("/download/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest();

            var id = uri.Segments[^1].Trim('/');
            if (!Guid.TryParse(id, out _))
                return Results.BadRequest();

            // Тянем с внутреннего HTTP1-эндпоинта Files (минуя nginx/TLS), как upload/view-прокси.
            var http1Base = config["FilesService:Http1Base"];
            var fetchUrl = string.IsNullOrEmpty(http1Base) ? url : $"{http1Base}/download/{id}";

            try
            {
                var client = httpFactory.CreateClient("files-upload");
                using var upstream = await client.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
                if (!upstream.IsSuccessStatusCode)
                    return Results.StatusCode(StatusCodes.Status502BadGateway);

                var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

                http.Response.ContentType = contentType;
                http.Response.Headers.CacheControl = "private, max-age=300";
                await upstream.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
                return Results.Empty;
            }
            catch
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        });

        // ───────── Публичная ссылка на файл ─────────
        // Анонимный резолв дружелюбного токена → 302 на публичный URL скачивания.
        // Резолв идёт через FilesServerApi (сервисный токен), т.к. пользователь не авторизован.
        app.MapGet("/s/{token}", async (string token, FilesServerApi.FilesServerApiClient filesServer) =>
        {
            try
            {
                var resp = await filesServer.ResolveShareAsync(new ResolveShareRequest { Token = token });
                return resp.Found
                    ? Results.Redirect(resp.DownloadUrl)
                    : Results.NotFound("Ссылка не найдена или была отозвана.");
            }
            catch (RpcException)
            {
                return Results.NotFound("Ссылка не найдена или была отозвана.");
            }
        });

        // Анонимный JSON для публичной страницы просмотра (/v/{token}, /m/{token}): метаданные + превью.
        app.MapGet("/s/{token}/info", async (string token, FilesServerApi.FilesServerApiClient filesServer) =>
        {
            try
            {
                var resp = await filesServer.ResolveShareAsync(new ResolveShareRequest { Token = token });
                if (!resp.Found)
                    return Results.NotFound(new { found = false });

                return Results.Json(new
                {
                    found = true,
                    name = resp.Name,
                    mediaKind = resp.MediaKind.ToString().ToLowerInvariant(),
                    previewUrl = resp.PreviewUrl,
                    // Прямой temp-URL оригинала (для встроенного плеера видео); тот же, что отдаёт редирект /s/{token}.
                    downloadUrl = resp.DownloadUrl,
                    imageWidth = resp.ImageWidth,
                    imageHeight = resp.ImageHeight,
                    fileSize = resp.FileSize,
                    downloadPath = "/s/" + token,
                });
            }
            catch (RpcException)
            {
                return Results.NotFound(new { found = false });
            }
        });

        // Анонимный JSON для публичной страницы папки (/f/{token}): листинг подпапок + файлов.
        // dir — подпапка внутри расшаренного поддерева (пусто = корень папки).
        app.MapGet("/f/{token}/list", async (string token, string? dir, FilesServerApi.FilesServerApiClient filesServer) =>
        {
            try
            {
                var resp = await filesServer.ResolveFolderShareAsync(new ResolveFolderShareRequest { Token = token, Dir = dir ?? "" });
                if (!resp.Found)
                    return Results.NotFound(new { found = false });

                return Results.Json(new
                {
                    found = true,
                    folderName = resp.FolderName,
                    currentDir = resp.CurrentDir,
                    currentName = resp.CurrentName,
                    subdirs = resp.Subdirs.Select(d => new { id = d.Id, name = d.Name }).ToArray(),
                    files = resp.Files.Select(f => new
                    {
                        fileId = f.FileId,
                        name = f.Name,
                        mediaKind = f.MediaKind.ToString().ToLowerInvariant(),
                        downloadUrl = f.DownloadUrl,
                        previewUrl = f.PreviewUrl,
                        fileSize = f.FileSize,
                        imageWidth = f.ImageWidth,
                        imageHeight = f.ImageHeight
                    }).ToArray()
                });
            }
            catch (RpcException)
            {
                return Results.NotFound(new { found = false });
            }
        });

        // Анонимный JSON для публичной страницы альбома (/al/{token}): элементы альбома, cursor-пагинация.
        app.MapGet("/al/{token}/list", async (string token, string? cursorAt, string? cursorId, FilesServerApi.FilesServerApiClient filesServer) =>
        {
            try
            {
                var req = new ResolveAlbumShareRequest { Token = token };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorAddedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorFileId = cursorId;

                var resp = await filesServer.ResolveAlbumShareAsync(req);
                if (!resp.Found)
                    return Results.NotFound(new { found = false });

                return Results.Json(new
                {
                    found = true,
                    albumName = resp.AlbumName,
                    description = resp.Description,
                    items = resp.Items.Select(f => new
                    {
                        fileId = f.FileId,
                        name = f.Name,
                        mediaKind = f.MediaKind.ToString().ToLowerInvariant(),
                        downloadUrl = f.DownloadUrl,
                        previewUrl = f.PreviewUrl,
                        fileSize = f.FileSize,
                        imageWidth = f.ImageWidth,
                        imageHeight = f.ImageHeight
                    }).ToArray(),
                    nextCursorAt = resp.NextCursorAddedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorFileId
                });
            }
            catch (RpcException)
            {
                return Results.NotFound(new { found = false });
            }
        });

        // Анонимный JSON для публичной страницы музыкального плейлиста (/mpl/{token}).
        app.MapGet("/mpl/{token}/list", async (string token, FilesServerApi.FilesServerApiClient filesServer) =>
        {
            try
            {
                var resp = await filesServer.ResolveMusicPlaylistShareAsync(new ResolveMusicPlaylistShareRequest { Token = token });
                if (!resp.Found)
                    return Results.NotFound(new { found = false });

                return Results.Json(new
                {
                    found = true,
                    playlistName = resp.PlaylistName,
                    description = resp.Description,
                    coverUrl = resp.CoverPreviewUrl,
                    items = resp.Items.Select(t => new
                    {
                        file = CloudJson.Media(t.File),
                        title = t.Title,
                        artist = t.Artist,
                        album = t.Album,
                        duration = t.DurationSeconds,
                        coverUrl = t.CoverUrl,
                        largeCoverUrl = t.LargeCoverUrl,
                        url = t.File.FileUrl
                    }).ToArray()
                });
            }
            catch (RpcException)
            {
                return Results.NotFound(new { found = false });
            }
        });

        // ───────── Защищённые страницы ─────────
        // Страницы приложения (/photos, /videos, /files, /favorites, /trash, /settings, /shared)
        // отдаёт React-SPA через SPA-fallback в Program.cs (UseStaticFiles + MapFallback).
        // Данные грузятся на клиенте через /api (включая /api/me и /api/settings/full).
    }

    private static bool TryValidateIconProxyUrl(HttpContext http, IConfiguration config, string? raw, out Uri target)
    {
        target = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        if (!uri.AbsolutePath.Contains("/download/", StringComparison.OrdinalIgnoreCase))
            return false;

        var requestHost = PublicHost(config) ?? http.Request.Host.Host;
        if (string.Equals(uri.Host, requestHost, StringComparison.OrdinalIgnoreCase))
        {
            target = uri;
            return true;
        }

        if (IsLoopbackHost(requestHost) && uri.IsLoopback)
        {
            target = uri;
            return true;
        }

        return false;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    private static string? PublicHost(IConfiguration config)
    {
        var value = config["App:PublicHost"];
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            return absolute.Host;

        return value.Split(':', 2)[0];
    }

}
