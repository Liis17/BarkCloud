using BarkCloud.GrpcServer;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Grpc.Core;

using System.Net;

namespace BarkCloud.Web;

public static class WebEndpoints
{
    private const string LoginPage = "Login Page Full.html";

    public static void MapWebEndpoints(this WebApplication app)
    {
        // Корень: на главную (Фото) если авторизован, иначе на логин
        app.MapGet("/", async (HttpContext http, AuthGateway auth) =>
            await auth.AuthenticateAsync(http) is not null
                ? Results.Redirect("/photos")
                : Results.Redirect("/login"));

        // ───────── Логин ─────────

        app.MapGet("/login", async (HttpContext http, AuthGateway auth, PageService pages, IConfiguration config) =>
        {
            if (await auth.AuthenticateAsync(http) is not null)
                return Results.Redirect("/photos");

            var html = await pages.RenderAsync(LoginPage, LoginVars(http, config, "default", null, null, null));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapPost("/login", async (HttpContext http, AuthGateway auth, PageService pages, IConfiguration config) =>
        {
            var form = await http.Request.ReadFormAsync();
            var login = form["login"].ToString();
            var password = form["password"].ToString();
            var otp = form["otp"].ToString();
            var remember = form.ContainsKey("remember");

            var result = await auth.LoginAsync(http, login, password, string.IsNullOrWhiteSpace(otp) ? null : otp, remember);

            switch (result.Outcome)
            {
                case LoginOutcome.Success:
                    return Results.Redirect("/photos");

                case LoginOutcome.NeedsOtp:
                case LoginOutcome.WrongOtp:
                    var twoFa = await pages.RenderAsync(LoginPage, LoginVars(http, config, "2fa", login, login, password));
                    return Results.Content(twoFa, "text/html; charset=utf-8");

                default:
                    var error = await pages.RenderAsync(LoginPage, LoginVars(http, config, "error", login, login, password));
                    return Results.Content(error, "text/html; charset=utf-8");
            }
        });

        app.MapMethods("/logout", ["GET", "POST"], async (HttpContext http, AuthGateway auth) =>
        {
            var user = await auth.AuthenticateAsync(http);
            await auth.LogoutAsync(http, user);
            return Results.Redirect("/login");
        });

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

        // ───────── Регистрация (с подтверждением кодом по почте) ─────────

        app.MapGet("/register", async (HttpContext http, AuthGateway auth, PageService pages, IConfiguration config) =>
        {
            if (await auth.AuthenticateAsync(http) is not null)
                return Results.Redirect("/photos");

            var html = await pages.RenderAsync(LoginPage, RegisterVars(http, config, null, "", "", "", ""));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // Шаг 1: создаёт черновик и отправляет код на почту → экран ввода кода.
        app.MapPost("/register", async (HttpContext http, RegistrationGateway registration, PageService pages, IConfiguration config) =>
        {
            var form = await http.Request.ReadFormAsync();
            var firstName = form["first_name"].ToString();
            var lastName = form["last_name"].ToString();
            var username = form["username"].ToString();
            var email = form["email"].ToString();
            var password = form["password"].ToString();

            var result = await registration.BeginAsync(http, firstName, lastName, username, email, password);

            // Режим без почты: аккаунт создан и сессия открыта сразу — без экрана ввода кода.
            if (result.Outcome == RegistrationOutcome.Success)
                return Results.Redirect("/photos");

            if (result.Outcome == RegistrationOutcome.PendingConfirmation)
            {
                var confirm = await pages.RenderAsync(LoginPage,
                    RegisterConfirmVars(http, config, result.CodeId!, email, password, null));
                return Results.Content(confirm, "text/html; charset=utf-8");
            }

            var html = await pages.RenderAsync(LoginPage,
                RegisterVars(http, config, result.Message, firstName, lastName, username, email));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // Шаг 2: проверяет код, ставит пароль и открывает сессию.
        app.MapPost("/register/confirm", async (HttpContext http, RegistrationGateway registration, PageService pages, IConfiguration config) =>
        {
            var form = await http.Request.ReadFormAsync();
            var codeId = form["code_id"].ToString();
            var code = form["otp"].ToString();
            var password = form["password"].ToString();
            var email = form["email"].ToString();

            var result = await registration.ConfirmAsync(http, codeId, code, password);

            if (result.Outcome == RegistrationOutcome.Success)
                return Results.Redirect("/photos");

            var html = await pages.RenderAsync(LoginPage,
                RegisterConfirmVars(http, config, codeId, email, password, result.Message ?? "Не удалось подтвердить код."));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ───────── Восстановление пароля «Забыли пароль?» (код по почте) ─────────

        app.MapGet("/forgot", async (HttpContext http, AuthGateway auth, PageService pages, IConfiguration config) =>
        {
            // Режим без почты: сброс пароля недоступен (доставить код некуда).
            if (!config.EmailEnabled())
                return Results.Redirect("/login");

            if (await auth.AuthenticateAsync(http) is not null)
                return Results.Redirect("/photos");

            var html = await pages.RenderAsync(LoginPage, ForgotVars(http, config, null, ""));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // Шаг 1: отправляет код сброса на почту → экран ввода кода и нового пароля.
        app.MapPost("/forgot", async (HttpContext http, PasswordResetGateway reset, PageService pages, IConfiguration config) =>
        {
            if (!config.EmailEnabled())
                return Results.Redirect("/login");

            var form = await http.Request.ReadFormAsync();
            var login = form["login"].ToString();

            var result = await reset.BeginAsync(http, login);

            if (result.Outcome == PasswordResetOutcome.PendingConfirmation)
            {
                var confirm = await pages.RenderAsync(LoginPage,
                    ForgotConfirmVars(http, config, result.ResetId!, login, null));
                return Results.Content(confirm, "text/html; charset=utf-8");
            }

            var html = await pages.RenderAsync(LoginPage, ForgotVars(http, config, result.Message, login));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // Шаг 2: проверяет код, ставит новый пароль и открывает сессию.
        app.MapPost("/forgot/confirm", async (HttpContext http, PasswordResetGateway reset, PageService pages, IConfiguration config) =>
        {
            if (!config.EmailEnabled())
                return Results.Redirect("/login");

            var form = await http.Request.ReadFormAsync();
            var resetId = form["reset_id"].ToString();
            var code = form["otp"].ToString();
            var password = form["password"].ToString();
            var login = form["login"].ToString();

            var result = await reset.ConfirmAsync(http, resetId, code, password);

            if (result.Outcome == PasswordResetOutcome.Success)
                return Results.Redirect("/photos");

            var html = await pages.RenderAsync(LoginPage,
                ForgotConfirmVars(http, config, resetId, login, result.Message ?? "Не удалось подтвердить код."));
            return Results.Content(html, "text/html; charset=utf-8");
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

        // Анонимный JSON для публичной страницы просмотра (/v/{token}): метаданные + превью.
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

    private static Dictionary<string, string?> LoginVars(
        HttpContext http, IConfiguration config, string flashKind, string? email, string? login, string? password)
        => new()
        {
            ["app.version"] = config.Value("App:Version", "v1.0.0"),
            ["server.host"] = config.Value("App:PublicHost", http.Request.Host.Value),
            ["server.tls"] = config.Value("App:TlsLabel", "TLS 1.3"),
            ["flash.kind"] = flashKind,
            ["email.enabled"] = config.EmailEnabled() ? "true" : "false",
            ["form.email"] = email ?? "",
            ["form.password_masked"] = "",
            ["form.attempts_left"] = "—",
            ["form.login"] = login ?? "",
            ["form.password"] = password ?? "",
            ["year"] = DateTime.UtcNow.Year.ToString()
        };

    private static Dictionary<string, string?> RegisterVars(
        HttpContext http, IConfiguration config, string? error,
        string firstName, string lastName, string username, string email)
        => new()
        {
            ["app.version"] = config.Value("App:Version", "v1.0.0"),
            ["server.host"] = config.Value("App:PublicHost", http.Request.Host.Value),
            ["server.tls"] = config.Value("App:TlsLabel", "TLS 1.3"),
            ["flash.kind"] = "register",
            ["email.enabled"] = config.EmailEnabled() ? "true" : "false",
            ["form.error"] = error ?? "",
            ["form.first_name"] = firstName,
            ["form.last_name"] = lastName,
            ["form.username"] = username,
            ["form.email"] = email,
            ["year"] = DateTime.UtcNow.Year.ToString()
        };

    private static Dictionary<string, string?> RegisterConfirmVars(
        HttpContext http, IConfiguration config, string codeId, string email, string password, string? error)
        => new()
        {
            ["app.version"] = config.Value("App:Version", "v1.0.0"),
            ["server.host"] = config.Value("App:PublicHost", http.Request.Host.Value),
            ["server.tls"] = config.Value("App:TlsLabel", "TLS 1.3"),
            ["flash.kind"] = "register_confirm",
            ["email.enabled"] = config.EmailEnabled() ? "true" : "false",
            ["form.error"] = error ?? "",
            ["form.code_id"] = codeId,
            ["form.email"] = email,
            ["form.password"] = password,
            ["year"] = DateTime.UtcNow.Year.ToString()
        };

    private static Dictionary<string, string?> ForgotVars(
        HttpContext http, IConfiguration config, string? error, string login)
        => new()
        {
            ["app.version"] = config.Value("App:Version", "v1.0.0"),
            ["server.host"] = config.Value("App:PublicHost", http.Request.Host.Value),
            ["server.tls"] = config.Value("App:TlsLabel", "TLS 1.3"),
            ["flash.kind"] = "forgot",
            ["email.enabled"] = config.EmailEnabled() ? "true" : "false",
            ["form.error"] = error ?? "",
            ["form.login"] = login,
            ["year"] = DateTime.UtcNow.Year.ToString()
        };

    private static Dictionary<string, string?> ForgotConfirmVars(
        HttpContext http, IConfiguration config, string resetId, string login, string? error)
        => new()
        {
            ["app.version"] = config.Value("App:Version", "v1.0.0"),
            ["server.host"] = config.Value("App:PublicHost", http.Request.Host.Value),
            ["server.tls"] = config.Value("App:TlsLabel", "TLS 1.3"),
            ["flash.kind"] = "forgot_confirm",
            ["email.enabled"] = config.EmailEnabled() ? "true" : "false",
            ["form.error"] = error ?? "",
            ["form.reset_id"] = resetId,
            ["form.login"] = login,
            ["year"] = DateTime.UtcNow.Year.ToString()
        };
}
