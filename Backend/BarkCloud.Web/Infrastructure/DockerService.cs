using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BarkCloud.Web.Infrastructure;

/// <summary>Результат действия над сервисом/контейнером.</summary>
public sealed record ServiceActionResult(bool Success, string Message, string? ErrorDetails = null);

/// <summary>Статус управляемого сервиса (для UI).</summary>
public sealed record ServiceStatus(string Service, string Container, string State, string Status, string Image, bool IsWeb);

/// <summary>Снимок состояния сервисов + доступность Docker (чтобы UI не оставался пустым при сбое).</summary>
public sealed record ServicesSnapshot(IReadOnlyList<ServiceStatus> Services, bool DockerOk, string? Error);

/// <summary>
/// Управление обновлением/перезапуском микросервисов бэкенда на той же машине через
/// смонтированный docker.sock (CLI <c>docker</c> / <c>docker compose</c>). Порт логики
/// из админ-панели BarkFluff под один локальный хост: без SSH и удалённых серверов.
///
/// Сам веб-контейнер нельзя пересоздать/остановить изнутри него же, поэтому обновление и
/// перезапуск веба выполняются через эфемерный helper-контейнер (см. <see cref="UpdateWebSelfAsync"/>).
/// Команды строятся через <see cref="ProcessStartInfo.ArgumentList"/> — аргументы передаются ОС
/// буквально, без shell-интерпретации (защита от инъекций).
/// </summary>
public sealed class DockerService
{
    private const string WebService = "web";
    private const string WebContainer = "cloud-web";

    // Управляемый набор: сервис docker compose -> имя контейнера. Инфраструктуру не трогаем.
    // Порядок важен для UpdateAll: configuration первым (от него зависят остальные).
    private static readonly (string Service, string Container)[] Managed =
    [
        ("configuration", "cloud-configuration"),
        ("identity", "cloud-identity"),
        ("users", "cloud-users"),
        ("files", "cloud-files"),
        ("notification", "cloud-notification"),
        (WebService, WebContainer),
    ];

    // Пути внутри контейнера web, куда смонтированы compose-файл и .env (см. docker-compose.yml).
    private const string ComposeFileInContainer = "/docker-compose.yml";
    private const string EnvFileInContainer = "/.env";

    private readonly ILogger<DockerService> _logger;

    public DockerService(ILogger<DockerService> logger) => _logger = logger;

    // ───────────────────────── Статус ─────────────────────────

    /// <summary>Статусы всех управляемых сервисов (running/exited/not_found и тег образа).</summary>
    public async Task<ServicesSnapshot> GetServicesStatusAsync()
    {
        var byName = new Dictionary<string, (string State, string Status, string Image)>();
        var dockerOk = true;
        string? error = null;

        try
        {
            var json = await RunDockerCommandAsync("ps", "--all", "--format", "{{json .}}");
            foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var root = JsonDocument.Parse(line.Trim()).RootElement;
                    var name = root.TryGetProperty("Names", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name)) continue;
                    byName[name] = (
                        root.TryGetProperty("State", out var s) ? s.GetString() ?? "" : "",
                        root.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "",
                        root.TryGetProperty("Image", out var im) ? im.GetString() ?? "" : "");
                }
                catch (JsonException ex) { _logger.LogWarning(ex, "Не разобрана строка docker ps: {Line}", line); }
            }
        }
        catch (Exception ex)
        {
            // Не валим запрос (иначе UI получает пустую страницу) — отдаём список сервисов и причину сбоя Docker.
            dockerOk = false;
            error = ex.Message;
            _logger.LogError(ex, "Не удалось получить список контейнеров");
        }

        var services = Managed.Select(m =>
        {
            var found = byName.TryGetValue(m.Container, out var info);
            return new ServiceStatus(
                m.Service,
                m.Container,
                found ? info.State : (dockerOk ? "not_found" : "unavailable"),
                found ? info.Status : (dockerOk ? "Контейнер не найден" : "Docker недоступен"),
                found ? info.Image : "",
                m.Service == WebService);
        }).ToList();

        return new ServicesSnapshot(services, dockerOk, error);
    }

    // ───────────────────────── Обновление ─────────────────────────

    /// <summary>Обновить образ и пересоздать один сервис (web — только через self-update).</summary>
    public async Task<ServiceActionResult> UpdateServiceAsync(string service)
    {
        if (!IsManagedNonWeb(service, out var error)) return error!;

        try
        {
            await PullAndRecreateAsync(service);
            await RunDockerCommandAsync("image", "prune", "-f");
            return new ServiceActionResult(true, $"Сервис {service} обновлён и пересоздан");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обновления сервиса {Service}", service);
            return new ServiceActionResult(false, $"Ошибка обновления сервиса {service}", ex.Message);
        }
    }

    /// <summary>Последовательно обновить все сервисы приложения (web исключён).</summary>
    public async Task<ServiceActionResult> UpdateAllServicesAsync()
    {
        var done = new List<string>();
        var errors = new List<string>();

        foreach (var (service, _) in Managed.Where(m => m.Service != WebService))
        {
            try
            {
                await PullAndRecreateAsync(service);
                done.Add(service);
                await Task.Delay(3000); // дать сервису подняться/инициализироваться
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обновления сервиса {Service}", service);
                errors.Add($"{service}: {ex.Message}");
            }
        }

        try { await RunDockerCommandAsync("image", "prune", "-f"); }
        catch (Exception ex) { _logger.LogWarning(ex, "Не удалось очистить неиспользуемые образы"); }

        return errors.Count == 0
            ? new ServiceActionResult(true, $"Обновлены: {string.Join(", ", done)}")
            : new ServiceActionResult(false, $"Обновлены: {string.Join(", ", done)}. Ошибки: {string.Join("; ", errors)}", string.Join("\n", errors));
    }

    // ───────────────────────── Жизненный цикл ─────────────────────────

    public Task<ServiceActionResult> RestartServiceAsync(string service)
        => RunContainerActionAsync(service, "restart", "перезапущен", "restart", "-t", "30");

    public Task<ServiceActionResult> StartServiceAsync(string service)
        => RunContainerActionAsync(service, "start", "запущен", "start");

    public Task<ServiceActionResult> StopServiceAsync(string service)
        => RunContainerActionAsync(service, "stop", "остановлен", "stop", "-t", "30");

    private async Task<ServiceActionResult> RunContainerActionAsync(
        string service, string verb, string doneWord, params string[] dockerArgsBeforeContainer)
    {
        if (!IsManagedNonWeb(service, out var error)) return error!;
        var container = ContainerOf(service);

        try
        {
            await RunDockerCommandAsync([.. dockerArgsBeforeContainer, container]);
            return new ServiceActionResult(true, $"Сервис {service} {doneWord}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка действия {Verb} над сервисом {Service}", verb, service);
            return new ServiceActionResult(false, $"Ошибка действия «{verb}» над сервисом {service}", ex.Message);
        }
    }

    // ───────────────────────── Self-update веба ─────────────────────────

    /// <summary>Обновить образ и пересоздать сам веб через detached helper-контейнер.</summary>
    public Task<ServiceActionResult> UpdateWebSelfAsync()
        => RunWebHelperAsync(
            "cloud-web-updater",
            withComposeMounts: true,
            innerCommand: (project, compose, env) =>
                $"sleep 2 && docker compose -p {project} --env-file {env} -f {compose} pull {WebService}" +
                $" && docker compose -p {project} --env-file {env} -f {compose} up --force-recreate -d {WebService}" +
                " && docker image prune -f",
            startedMessage: "Обновление веб-клиента запущено");

    /// <summary>Перезапустить сам веб через detached helper-контейнер.</summary>
    public Task<ServiceActionResult> RestartWebSelfAsync()
        => RunWebHelperAsync(
            "cloud-web-restarter",
            withComposeMounts: false,
            innerCommand: (_, _, _) => $"sleep 2 && docker restart {WebContainer}",
            startedMessage: "Перезапуск веб-клиента запущен");

    private async Task<ServiceActionResult> RunWebHelperAsync(
        string helperName, bool withComposeMounts, Func<string, string, string, string> innerCommand, string startedMessage)
    {
        try
        {
            var image = (await RunDockerCommandAsync("inspect", "--format", "{{.Config.Image}}", WebContainer)).Trim();
            var dockerSock = await GetMountSourceAsync(WebContainer, "/var/run/docker.sock");
            var project = await GetComposeProjectAsync();

            await TryRemoveContainerAsync(helperName);

            var args = new List<string>
            {
                "run", "-d", "--rm", "--name", helperName, "--user", "root",
                "-e", "DOCKER_HOST=unix:///var/run/docker.sock",
                "-e", "DOCKER_CONFIG=/root/.docker",
                "-v", $"{dockerSock}:/var/run/docker.sock",
            };

            // helper тянет образ из приватного registry — пробросим креды docker, если они смонтированы в web
            var dockerConfig = await GetMountSourceAsync(WebContainer, "/root/.docker/config.json");
            if (!string.IsNullOrEmpty(dockerConfig))
                args.AddRange(["-v", $"{dockerConfig}:/root/.docker/config.json:ro"]);

            string compose = ComposeFileInContainer, env = EnvFileInContainer;
            if (withComposeMounts)
            {
                // монтируем compose и env по их РЕАЛЬНЫМ хостовым путям, чтобы относительные пути
                // внутри compose (./.env, ./nginx и т.п.) разрешались корректно на хосте
                compose = await GetMountSourceAsync(WebContainer, ComposeFileInContainer);
                env = await GetMountSourceAsync(WebContainer, EnvFileInContainer);
                args.AddRange(["-v", $"{compose}:{compose}:ro", "-v", $"{env}:{env}:ro"]);
            }

            args.AddRange(["--entrypoint", "sh", image, "-c", innerCommand(project, compose, env)]);

            await RunDockerCommandAsync([.. args]);
            return new ServiceActionResult(true, startedMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка запуска helper-контейнера {Helper}", helperName);
            return new ServiceActionResult(false, "Не удалось запустить операцию над веб-клиентом", ex.Message);
        }
    }

    // ───────────────────────── Внутреннее ─────────────────────────

    private async Task PullAndRecreateAsync(string service)
    {
        var project = await GetComposeProjectAsync();
        await RunDockerComposeCommandAsync("-p", project, "--env-file", EnvFileInContainer, "-f", ComposeFileInContainer, "pull", service);
        await RunDockerComposeCommandAsync("-p", project, "--env-file", EnvFileInContainer, "-f", ComposeFileInContainer, "up", "--force-recreate", "-d", service);
    }

    private static bool IsManaged(string service) => Managed.Any(m => m.Service == service);
    private static string ContainerOf(string service) => Managed.First(m => m.Service == service).Container;

    /// <summary>Сервис из белого списка и не web (web — только self-методы).</summary>
    private static bool IsManagedNonWeb(string service, out ServiceActionResult? error)
    {
        if (!IsManaged(service))
            error = new ServiceActionResult(false, $"Неизвестный сервис: {service}");
        else if (service == WebService)
            error = new ServiceActionResult(false, "Веб-клиент управляется только через self-update / self-restart");
        else
            error = null;
        return error is null;
    }

    /// <summary>Имя docker compose проекта берём из метки запущенного web-контейнера (без хардкода).</summary>
    private async Task<string> GetComposeProjectAsync()
    {
        try
        {
            var project = (await RunDockerCommandAsync(
                "inspect", "--format", "{{ index .Config.Labels \"com.docker.compose.project\" }}", WebContainer)).Trim();
            if (!string.IsNullOrEmpty(project)) return project;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Не удалось определить имя compose-проекта, fallback=barkcloud"); }
        return "barkcloud";
    }

    /// <summary>Host-путь bind mount'а по destination внутри контейнера ("" если не найден).</summary>
    private async Task<string> GetMountSourceAsync(string container, string destination)
    {
        var template = "{{range .Mounts}}{{if eq .Destination \"" + destination + "\"}}{{.Source}}{{end}}{{end}}";
        return (await RunDockerCommandAsync("inspect", "--format", template, container)).Trim();
    }

    private async Task TryRemoveContainerAsync(string container)
    {
        try { await RunDockerCommandAsync("rm", "-f", container); }
        catch { /* контейнера нет — это нормально */ }
    }

    private Task<string> RunDockerCommandAsync(params string[] args) => RunProcessAsync("docker", args);

    private Task<string> RunDockerComposeCommandAsync(params string[] args) => RunProcessAsync("docker", ["compose", .. args]);

    private static async Task<string> RunProcessAsync(string fileName, string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Registry публичный (auth нужен только для push в CI), креды для pull не требуются.
        // Пустой DOCKER_CONFIG, чтобы CLI не читал config.json хоста с "credsStore": "desktop"
        // и не пытался вызвать docker-credential-desktop (которого нет в alpine-контейнере).
        startInfo.Environment["DOCKER_CONFIG"] = "/tmp/barkcloud-docker";
        // Подключаемся напрямую к смонтированному сокету и игнорируем currentContext из config.json
        // (на хосте это "desktop-linux", чьих метаданных внутри контейнера нет — иначе CLI падает).
        startInfo.Environment["DOCKER_HOST"] = "unix:///var/run/docker.sock";
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} → код {process.ExitCode}: {errors}".Trim());

        return output.ToString().Trim();
    }
}
