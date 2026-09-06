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
    public sealed record BranchRequest(string? Branch);

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

        api.MapGet("/services", async (
            HttpContext http,
            AuthGateway auth,
            AdminGate admin,
            DockerService docker,
            ComposeImageService compose,
            DockerRegistryService registry,
            MaintenanceOperationStore maintenance) =>
            await Guard(http, auth, admin) is { } fail
                ? fail
                : Results.Ok(await GetEnrichedServicesAsync(docker, compose, registry, maintenance)));

        api.MapGet("/branches", async (
            HttpContext http,
            AuthGateway auth,
            AdminGate admin,
            DockerService docker,
            ComposeImageService compose) =>
        {
            if (await Guard(http, auth, admin) is { } fail) return fail;

            IReadOnlyDictionary<string, ComposeImageInfo> images;
            try
            {
                images = await compose.GetImagesAsync();
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    currentBranch = (string?)null,
                    branches = ComposeImageService.Branches,
                    services = Array.Empty<object>(),
                    error = $"Не удалось прочитать docker-compose.yml: {ex.Message}",
                });
            }

            var snapshot = await docker.GetServicesStatusAsync();
            var runningByService = snapshot.Services.ToDictionary(
                service => service.Service,
                service => ComposeImageService.BranchFromImage(service.Image)
                    ?? (images.TryGetValue(service.ComposeService, out var composeImage) ? composeImage.Branch : null),
                StringComparer.OrdinalIgnoreCase);
            var branchInfo = images.Values
                .Select(image => new
                {
                    image,
                    service = DockerService.LogicalServiceNameForCompose(image.Service),
                })
                .Where(item => item.service is not null)
                .OrderBy(item => item.service, StringComparer.Ordinal)
                .Select(item => new
                {
                    service = item.service!,
                    composeService = item.image.Service,
                    branch = item.image.Branch,
                    runningBranch = runningByService.TryGetValue(item.service!, out var running)
                        ? running
                        : null,
                    branches = ComposeImageService.Branches,
                })
                .ToList();

            var currentBranch = branchInfo.Select(item => item.branch).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? branchInfo[0].branch
                : null;
            return Results.Ok(new { currentBranch, branches = ComposeImageService.Branches, services = branchInfo });
        });

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

        api.MapPost("/services/{service}/branch", async (
            HttpContext http,
            AuthGateway auth,
            AdminGate admin,
            ComposeImageService compose,
            DockerRegistryService registry,
            DockerService docker,
            DeploymentJobService jobs,
            string service,
            BranchRequest request) =>
        {
            if (await Guard(http, auth, admin) is { } fail) return fail;
            if (!DockerService.TryGetManagedService(service, out var canonical))
                return Results.BadRequest(new { message = $"Неизвестный сервис: {service}" });

            var branch = request.Branch?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!ComposeImageService.IsKnownBranch(branch))
                return Results.BadRequest(new { message = $"Неизвестный канал: {branch}" });

            IReadOnlyDictionary<string, ComposeImageInfo> images;
            try
            {
                images = await compose.GetImagesAsync();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    message = "Не удалось прочитать docker-compose.yml",
                    diagnostic = ex.Message,
                });
            }

            var composeService = DockerService.ComposeServiceNameFor(canonical);
            if (!images.TryGetValue(composeService, out var image))
                return Results.NotFound(new { message = $"Сервис {canonical} не найден в docker-compose.yml" });

            var snapshot = await docker.GetServicesStatusAsync();
            var current = snapshot.Services.FirstOrDefault(item =>
                string.Equals(item.Service, canonical, StringComparison.OrdinalIgnoreCase));
            var runningBranch = ComposeImageService.BranchFromImage(current?.Image);
            if (string.Equals(image.Branch, branch, StringComparison.OrdinalIgnoreCase)
                && string.Equals(runningBranch, branch, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new { message = $"{canonical} уже работает на канале {branch}" });
            }

            var repository = ComposeImageService.Repository(image.BaseRepository, branch);
            if (!await registry.RepositoryExistsAsync(repository))
                return Results.BadRequest(new
                {
                    message = $"Репозиторий {repository} не найден или реестр недоступен",
                });

            var job = jobs.EnqueueBranchSwitch(canonical, branch);
            return Results.Ok(new
            {
                jobId = job.Id,
                message = $"{canonical} переключается на канал {branch}",
            });
        });

        api.MapPost("/update-all", async (HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs) =>
            await EnqueueAll(http, auth, admin, jobs, DeploymentJobKind.Update));

        api.MapPost("/restart-all", async (HttpContext http, AuthGateway auth, AdminGate admin, DeploymentJobService jobs) =>
            await EnqueueAll(http, auth, admin, jobs, DeploymentJobKind.Restart));

        api.MapPost("/update-available", async (
            HttpContext http,
            AuthGateway auth,
            AdminGate admin,
            DockerService docker,
            ComposeImageService compose,
            DockerRegistryService registry,
            MaintenanceOperationStore maintenance,
            DeploymentJobService jobs) =>
        {
            if (await Guard(http, auth, admin) is { } fail) return fail;

            var snapshot = await GetEnrichedServicesAsync(docker, compose, registry, maintenance);
            var services = snapshot.Services
                .Where(service => !string.IsNullOrWhiteSpace(service.ComposeService) && service.UpdateAvailable == true)
                .Select(service => service.Service)
                .ToList();
            if (services.Count == 0)
            {
                return Results.Ok(new
                {
                    jobId = (Guid?)null,
                    updated = 0,
                    message = "Доступных обновлений нет",
                });
            }

            var job = jobs.EnqueueUpdate(services);
            return Results.Ok(new
            {
                jobId = job.Id,
                updated = services.Count,
                message = $"Обновление доступных сервисов ({services.Count}) поставлено в очередь",
            });
        });

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
            Run(http, auth, admin, () => docker.UpdateWebSelfAsync()));

        api.MapPost("/web/restart-self", (HttpContext http, AuthGateway auth, AdminGate admin, DockerService docker) =>
            Run(http, auth, admin, () => docker.RestartWebSelfAsync()));
    }

    private static async Task<ServicesSnapshot> GetEnrichedServicesAsync(
        DockerService docker,
        ComposeImageService compose,
        DockerRegistryService registry,
        MaintenanceOperationStore maintenance)
    {
        var snapshot = await docker.GetServicesStatusAsync();
        IReadOnlyDictionary<string, ComposeImageInfo> composeImages;
        try
        {
            composeImages = await compose.GetImagesAsync();
        }
        catch
        {
            composeImages = new Dictionary<string, ComposeImageInfo>(StringComparer.OrdinalIgnoreCase);
        }

        var services = await Task.WhenAll(snapshot.Services.Select(async service =>
        {
            composeImages.TryGetValue(service.ComposeService, out var composeImage);
            var composeReference = composeImage is null ? null : ComposeImageService.ImageReference(composeImage);
            var image = DockerRegistryService.ResolveImageReference(service.Image, composeReference);
            var imageDigest = service.ImageDigest;
            if (image is not null && !string.Equals(image, service.Image, StringComparison.OrdinalIgnoreCase))
                imageDigest = await docker.GetContainerImageDigestAsync(service.Container, image);
            var version = service.State == "unavailable"
                ? new ImageVersionStatus
                {
                    Branch = composeImage?.Branch,
                    Tag = composeImage?.Tag,
                    State = ImageVersionState.Unknown,
                }
                : await registry.GetVersionStatusAsync(image, imageDigest);

            // Если контейнер отсутствует, показываем канал из Compose — именно его обновит очередь.
            if (version.Branch is null && composeImage is not null)
            {
                version = version with
                {
                    Branch = composeImage.Branch,
                    Tag = composeImage.Tag,
                };
            }

            // Для отсутствующего контейнера Compose даёт только целевой образ, а не текущую версию.
            // Latest остаётся видимой, но сравнение обновления до запуска было бы ложным.
            if (service.State == "not_found")
            {
                version = version with
                {
                    CurrentVersion = null,
                    UpdateAvailable = null,
                    State = ImageVersionState.Unknown,
                    Error = "Контейнер не найден; версия станет известна после запуска",
                };
            }

            return service with
            {
                ComposeService = composeImage?.Service ?? string.Empty,
                Version = version,
            };
        }));

        return snapshot with
        {
            Services = services,
            LastMaintenance = await maintenance.ReadAsync(),
        };
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
        DeploymentJobKind.SwitchBranch => "переключение канала",
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
