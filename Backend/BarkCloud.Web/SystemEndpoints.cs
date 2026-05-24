using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web;

/// <summary>
/// Эндпоинты обслуживания бэкенда (обновление/перезапуск микросервисов) для страницы настроек.
/// Доступ: пользователь должен быть авторизован И раздел разблокирован админ-паролем
/// (см. <see cref="AdminGate"/>), кроме самого <c>unlock</c>, которому нужна только авторизация.
/// </summary>
public static class SystemEndpoints
{
    public sealed record UnlockRequest(string? Password);

    public static void MapSystemEndpoints(this WebApplication app)
    {
        // Лёгкий health-чек для опроса страницей «обновление идёт» (анонимный).
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        var api = app.MapGroup("/api/system");

        // ───────── Разблокировка по паролю ─────────

        api.MapPost("/unlock", async (HttpContext http, AuthGateway auth, AdminGate admin, UnlockRequest body) =>
        {
            if (await auth.AuthenticateAsync(http) is null) return Results.Unauthorized();
            if (!admin.Enabled) return Results.BadRequest(new { message = "Админ-доступ не настроен" });
            return admin.Unlock(http, body.Password)
                ? Results.Ok(new { unlocked = true })
                : Results.BadRequest(new { message = "Неверный пароль" });
        });

        api.MapPost("/lock", async (HttpContext http, AuthGateway auth, AdminGate admin) =>
        {
            if (await auth.AuthenticateAsync(http) is null) return Results.Unauthorized();
            admin.Lock(http);
            return Results.Ok(new { unlocked = false });
        });

        // ───────── Статус ─────────

        api.MapGet("/services", async (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker) =>
            await Guard(http, auth, admin) is { } fail ? fail : Results.Ok(await docker.GetServicesStatusAsync()));

        // ───────── Действия над сервисами ─────────

        api.MapPost("/services/{service}/update", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker, string service) =>
            Run(http, auth, admin, () => docker.UpdateServiceAsync(service)));

        api.MapPost("/services/{service}/restart", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker, string service) =>
            Run(http, auth, admin, () => docker.RestartServiceAsync(service)));

        api.MapPost("/services/{service}/start", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker, string service) =>
            Run(http, auth, admin, () => docker.StartServiceAsync(service)));

        api.MapPost("/services/{service}/stop", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker, string service) =>
            Run(http, auth, admin, () => docker.StopServiceAsync(service)));

        api.MapPost("/update-all", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker) =>
            Run(http, auth, admin, docker.UpdateAllServicesAsync));

        // ───────── Self-update веба ─────────

        api.MapPost("/web/update-self", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker) =>
            Run(http, auth, admin, docker.UpdateWebSelfAsync));

        api.MapPost("/web/restart-self", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker) =>
            Run(http, auth, admin, docker.RestartWebSelfAsync));
    }

    /// <summary>401, если не авторизован; 403, если раздел не разблокирован; иначе null.</summary>
    private static async Task<IResult?> Guard(HttpContext http, AuthGateway auth, AdminGate admin)
    {
        if (await auth.AuthenticateAsync(http) is null) return Results.Unauthorized();
        if (!admin.IsUnlocked(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        return null;
    }

    private static async Task<IResult> Run(
        HttpContext http, AuthGateway auth, AdminGate admin, Func<Task<ServiceActionResult>> action)
    {
        if (await Guard(http, auth, admin) is { } fail) return fail;
        var result = await action();
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }
}
