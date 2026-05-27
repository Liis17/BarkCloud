using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

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
            if (await auth.AuthenticateAsync(http) is not null)
                return Results.Redirect("/photos");

            var html = await pages.RenderAsync(LoginPage, ForgotVars(http, config, null, ""));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // Шаг 1: отправляет код сброса на почту → экран ввода кода и нового пароля.
        app.MapPost("/forgot", async (HttpContext http, PasswordResetGateway reset, PageService pages, IConfiguration config) =>
        {
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

        // ───────── Защищённые страницы ─────────
        // Страницы приложения (/photos, /videos, /files, /favorites, /trash, /settings, /shared)
        // отдаёт React-SPA через SPA-fallback в Program.cs (UseStaticFiles + MapFallback).
        // Данные грузятся на клиенте через /api (включая /api/me и /api/settings/full).
    }

    private static Dictionary<string, string?> LoginVars(
        HttpContext http, IConfiguration config, string flashKind, string? email, string? login, string? password)
        => new()
        {
            ["app.version"] = config.Value("App:Version", "v1.0.0"),
            ["server.host"] = config.Value("App:PublicHost", http.Request.Host.Value),
            ["server.tls"] = config.Value("App:TlsLabel", "TLS 1.3"),
            ["flash.kind"] = flashKind,
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
            ["form.error"] = error ?? "",
            ["form.reset_id"] = resetId,
            ["form.login"] = login,
            ["year"] = DateTime.UtcNow.Year.ToString()
        };
}
