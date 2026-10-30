using BarkCloud.GrpcServer;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Grpc.Core;

using System.Text.Json;

namespace BarkCloud.Web;

public static class WebEndpoints
{
    private const string SafeImageContentSecurityPolicy = "default-src 'none'; sandbox; base-uri 'none'; form-action 'none'";
    private static readonly HashSet<string> SafeImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/avif", "image/bmp"
    };
    private const int MaxIconBytes = 2 * 1024 * 1024;

    public static void MapWebEndpoints(this WebApplication app)
    {
        // Корень: на главную (Фото) если авторизован, иначе на логин
        app.MapGet("/", async (HttpContext http, AuthGateway auth) =>
            await auth.AuthenticateAsync(http) is not null
                ? Results.Redirect("/photos")
                : Results.Redirect("/login"));

        app.MapImageProxyEndpoints();

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
                    videoQualities = resp.VideoQualities.Select(q => new
                    {
                        height = q.Height,
                        width = q.Width,
                        fileSize = q.FileSize,
                        isOriginal = q.IsOriginal,
                        url = q.Url
                    }).ToArray(),
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
                        imageHeight = f.ImageHeight,
                        videoQualities = f.VideoQualities.Select(q => new
                        {
                            height = q.Height,
                            width = q.Width,
                            fileSize = q.FileSize,
                            isOriginal = q.IsOriginal,
                            url = q.Url
                        }).ToArray()
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
                        imageHeight = f.ImageHeight,
                        videoQualities = f.VideoQualities.Select(q => new
                        {
                            height = q.Height,
                            width = q.Width,
                            fileSize = q.FileSize,
                            isOriginal = q.IsOriginal,
                            url = q.Url
                        }).ToArray()
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

    internal static void MapImageProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/head/icon", async (HttpContext http, IHttpClientFactory httpFactory, IConfiguration config, string? url) =>
        {
            SetSafeImageResponseHeaders(http);
            if (!TryParseImageDownloadId(url, out var id))
                return Results.BadRequest();

            var http1Base = config["FilesService:Http1Base"];
            if (string.IsNullOrWhiteSpace(http1Base))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            try
            {
                var client = httpFactory.CreateClient("files-upload");
                var target = $"{http1Base.TrimEnd('/')}/download/{id}";
                using var resp = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                    return Results.StatusCode(StatusCodes.Status502BadGateway);

                var contentType = resp.Content.Headers.ContentType?.MediaType;
                if (!IsSafeImageContentType(contentType))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

                var bytes = await ReadLimitedAsync(resp.Content, MaxIconBytes, http.RequestAborted);
                if (bytes is null)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                http.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.File(bytes, contentType!);
            }
            catch
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        });

        // Браузерная копия пикселей через same-origin; источник ограничен ID blob Files.
        app.MapGet("/api/files/image", async (HttpContext http, IHttpClientFactory httpFactory, IConfiguration config, string? url) =>
        {
            SetSafeImageResponseHeaders(http);
            if (!TryParseImageDownloadId(url, out var id))
                return Results.BadRequest();

            var http1Base = config["FilesService:Http1Base"];
            if (string.IsNullOrWhiteSpace(http1Base))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            try
            {
                var client = httpFactory.CreateClient("files-upload");
                var fetchUrl = $"{http1Base.TrimEnd('/')}/download/{id}";
                using var upstream = await client.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
                if (!upstream.IsSuccessStatusCode)
                    return Results.StatusCode(StatusCodes.Status502BadGateway);

                var contentType = upstream.Content.Headers.ContentType?.MediaType;
                if (!IsSafeImageContentType(contentType))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

                http.Response.ContentType = contentType!;
                http.Response.Headers.CacheControl = "private, max-age=300";
                await upstream.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
                return Results.Empty;
            }
            catch
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        });
    }

    private static bool TryParseImageDownloadId(string? raw, out Guid id)
    {
        id = default;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.AbsolutePath.Contains("/download/", StringComparison.OrdinalIgnoreCase))
            return false;

        return Guid.TryParse(uri.Segments[^1].Trim('/'), out id);
    }

    private static void SetSafeImageResponseHeaders(HttpContext http)
    {
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Security-Policy"] = SafeImageContentSecurityPolicy;
    }

    private static bool IsSafeImageContentType(string? contentType) =>
        contentType is not null && SafeImageContentTypes.Contains(contentType.Trim());

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        var capacity = content.Headers.ContentLength is { } length ? (int)Math.Min(length, maxBytes) : 0;
        using var output = new MemoryStream(capacity);
        var buffer = new byte[81920];

        while (true)
        {
            var remaining = maxBytes - (int)output.Length;
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken);
            if (read == 0)
                return output.ToArray();
            if (read > remaining)
                return null;

            output.Write(buffer, 0, read);
        }
    }

}
