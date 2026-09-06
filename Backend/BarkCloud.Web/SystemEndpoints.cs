using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using System.Globalization;

namespace BarkCloud.Web;

/// <summary>
/// Эндпоинты обслуживания бэкенда для страницы настроек.
/// Все Docker-мутации проходят через одну серверную очередь, поэтому браузер не
/// управляет последовательностью шагов и не теряет задачу при перезагрузке страницы.
/// </summary>
public static class SystemEndpoints
{
    public sealed record UnlockRequest(string? Password);

    public static void MapSystemEndpoints(this WebApplication app)
    {
        // Лёгкий health-чек для страниц ожидания после перезапуска/обновления web.
        app.MapGet("/healthz", (HttpContext http) =>
        {
            http.Response.Headers["X-BarkCloud-Started-At"] =
                WebRuntime.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { status = "ok" });
        });

        // Эти страницы анонимны: cookie авторизации переживает пересоздание web,
        // а саму страницу ожидания нужно открыть до остановки текущего контейнера.
        app.MapGet("/updating", (HttpContext http, PageService pages) =>
            RenderWaitPage(http, pages, "updating.html"));
        app.MapGet("/restarting", (HttpContext http, PageService pages) =>
            RenderWaitPage(http, pages, "restarting.html"));
        app.MapGet("/maintenance-wait.js", async (HttpContext http, PageService pages) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Content(await pages.ReadRawAsync("maintenance-wait.js"), "application/javascript; charset=utf-8");
        });

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

        api.MapPost("/services/{service}/update", (
            HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs, string service) =>
            EnqueueSingle(http, auth, admin, jobs, service, DeploymentJobKind.Update, jobs.EnqueueUpdate));

        api.MapPost("/services/{service}/restart", (
            HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs, string service) =>
            EnqueueSingle(http, auth, admin, jobs, service, DeploymentJobKind.Restart, jobs.EnqueueRestart));

        api.MapPost("/services/{service}/start", (
            HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs, string service) =>
            EnqueueSingle(http, auth, admin, jobs, service, DeploymentJobKind.Start, jobs.EnqueueStart));

        api.MapPost("/services/{service}/stop", (
            HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs, string service) =>
            EnqueueSingle(http, auth, admin, jobs, service, DeploymentJobKind.Stop, jobs.EnqueueStop));

        api.MapPost("/update-all", async (HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs) =>
            await EnqueueAll(http, auth, admin, jobs, DeploymentJobKind.Update));

        api.MapPost("/restart-all", async (HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs) =>
            await EnqueueAll(http, auth, admin, jobs, DeploymentJobKind.Restart));

        // ───────── Состояние задач ─────────

        api.MapGet("/deploy/jobs", async (HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs) =>
            await Guard(http, auth, admin) is { } fail ? fail : Results.Ok(jobs.GetRecentJobs()));

        api.MapGet("/deploy/jobs/{id:guid}", async (
            HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs, Guid id) =>
        {
            if (await Guard(http, auth, admin) is { } fail) return fail;
            var job = jobs.GetJob(id);
            return job is null ? Results.NotFound(new { message = "Задача обслуживания не найдена" }) : Results.Ok(job);
        });

        // ───────── Self-update веба ─────────

        api.MapPost("/web/update-self", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker) =>
            Run(http, auth, admin, docker.UpdateWebSelfAsync));

        api.MapPost("/web/restart-self", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker) =>
            Run(http, auth, admin, docker.RestartWebSelfAsync));
    }

    private static async Task<IResult> RenderWaitPage(HttpContext http, PageService pages, string fileName)
    {
        http.Response.Headers.CacheControl = "no-store";
        var html = await pages.RenderAsync(fileName, new Dictionary<string, string?>
        {
            ["BARKCLOUD_STARTED_AT_UTC"] = WebRuntime.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        });
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static async Task<IResult> EnqueueSingle(
        HttpContext http,
        AuthGateway auth,
        AdminGate admin,
        DeploymentJobService jobs,
        string service,
        DeploymentJobKind kind,
        Func<IEnumerable<string>, DeploymentJob> enqueue)
    {
        if (await Guard(http, auth, admin) is { } fail) return fail;
        if (!DockerService.TryGetManagedNonWebService(service, out var canonical))
            return Results.BadRequest(new { message = $"Неизвестный или недоступный сервис: {service}" });

        var job = enqueue([canonical]);
        return Results.Ok(new
        {
            jobId = job.Id,
            message = $"Операция «{KindLabel(kind)}» для {canonical} поставлена в очередь",
        });
    }

    private static async Task<IResult> EnqueueAll(
        HttpContext http,
        AuthGateway auth,
        AdminGate admin,
        DeploymentJobService jobs,
        DeploymentJobKind kind)
    {
        if (await Guard(http, auth, admin) is { } fail) return fail;

        try
        {
            var job = await jobs.EnqueueAllAsync(kind);
            return Results.Ok(new
            {
                jobId = job.Id,
                message = $"Операция «{KindLabel(kind)}» поставлена в очередь",
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string KindLabel(DeploymentJobKind kind) => kind switch
    {
        DeploymentJobKind.Update => "обновление",
        DeploymentJobKind.Restart => "перезапуск",
        DeploymentJobKind.Start => "запуск",
        DeploymentJobKind.Stop => "остановка",
        _ => "операция",
    };

    /// <summary>401, если не авторизован; 403, если раздел не разблокирован админ-паролем.</summary>
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
