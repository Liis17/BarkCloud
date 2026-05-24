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

        // ───────── Регистрация (без подтверждения по почте и 2FA) ─────────

        app.MapGet("/register", async (HttpContext http, AuthGateway auth, PageService pages, IConfiguration config) =>
        {
            if (await auth.AuthenticateAsync(http) is not null)
                return Results.Redirect("/photos");

            var html = await pages.RenderAsync(LoginPage, RegisterVars(http, config, null, "", "", "", ""));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapPost("/register", async (HttpContext http, RegistrationGateway registration, PageService pages, IConfiguration config) =>
        {
            var form = await http.Request.ReadFormAsync();
            var firstName = form["first_name"].ToString();
            var lastName = form["last_name"].ToString();
            var username = form["username"].ToString();
            var email = form["email"].ToString();
            var password = form["password"].ToString();

            var result = await registration.RegisterAsync(http, firstName, lastName, username, email, password);

            if (result.Outcome == RegistrationOutcome.Success)
                return Results.Redirect("/photos");

            var html = await pages.RenderAsync(LoginPage,
                RegisterVars(http, config, result.Message, firstName, lastName, username, email));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ───────── Защищённые страницы ─────────

        app.MapGet("/photos", (HttpContext http, AuthGateway auth, PageDataBuilder data, PageService pages) =>
            ServePage(http, auth, data, pages, "Photos.html", data.BuildPhotosJsonAsync));

        app.MapGet("/files", (HttpContext http, AuthGateway auth, PageDataBuilder data, PageService pages) =>
            ServePage(http, auth, data, pages, "Files.html", data.BuildFilesJsonAsync));

        app.MapGet("/settings", (HttpContext http, AuthGateway auth, PageDataBuilder data, PageService pages) =>
            ServePage(http, auth, data, pages, "Settings.html", user => data.BuildSettingsJsonAsync(user, http)));

        // Видео и Общие пока без backing-данных — отдаём каркас, страница использует свой demo-fallback
        app.MapGet("/videos", (HttpContext http, AuthGateway auth, PageDataBuilder data, PageService pages) =>
            ServePage(http, auth, data, pages, "Videos.html", _ => Task.FromResult(string.Empty)));

        app.MapGet("/shared", (HttpContext http, AuthGateway auth, PageDataBuilder data, PageService pages) =>
            ServePage(http, auth, data, pages, "Shared.html", _ => Task.FromResult(string.Empty)));

        // ───────── Статические ресурсы страниц ─────────

        app.MapGet("/shared.jsx", async (HttpContext http, AuthGateway auth, PageDataBuilder data, PageService pages) =>
        {
            var user = await auth.AuthenticateAsync(http);
            IReadOnlyDictionary<string, string?> vars = user is null
                ? new Dictionary<string, string?>()
                : await data.BuildShellAsync(user, http);

            var js = await pages.RenderAsync("shared.jsx", vars);
            return Results.Content(js, "application/javascript; charset=utf-8");
        });

        app.MapGet("/shared.css", async (PageService pages) =>
            Results.Content(await pages.ReadRawAsync("shared.css"), "text/css; charset=utf-8"));
    }

    private static async Task<IResult> ServePage(
        HttpContext http,
        AuthGateway auth,
        PageDataBuilder data,
        PageService pages,
        string file,
        Func<WebUser, Task<string>> jsonFactory)
    {
        var user = await auth.AuthenticateAsync(http);
        if (user is null)
            return Results.Redirect("/login");

        var vars = await data.BuildShellAsync(user, http);
        vars["page_data_json"] = await jsonFactory(user);

        var html = await pages.RenderAsync(file, vars);
        return Results.Content(html, "text/html; charset=utf-8");
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
}
