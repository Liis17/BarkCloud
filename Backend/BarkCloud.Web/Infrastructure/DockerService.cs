using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BarkCloud.Web.Infrastructure;

/// <summary>Результат действия над сервисом/контейнером.</summary>
public sealed record ServiceActionResult(
    bool Success,
    string Message,
    string? ErrorDetails = null,
    string? OperationId = null);

/// <summary>Статус управляемого сервиса (для UI).</summary>
public sealed record ServiceStatus(string Service, string Container, string State, string Status, string Image, bool IsWeb)
{
    public string ComposeService { get; init; } = string.Empty;
    public string? ImageDigest { get; init; }
    public ImageVersionStatus Version { get; init; } = new();
    public string? Branch => Version.Branch;
    public string? CurrentVersion => Version.CurrentVersion;
    public string? LatestVersion => Version.LatestVersion;
    public bool? UpdateAvailable => Version.UpdateAvailable;
    public string VersionState => Version.State;
    public string? VersionError => Version.Error;
}

/// <summary>Снимок состояния сервисов + доступность Docker (чтобы UI не оставался пустым при сбое).</summary>
public sealed record ServicesSnapshot(IReadOnlyList<ServiceStatus> Services, bool DockerOk, string? Error)
{
    public MaintenanceOperationStatus? LastMaintenance { get; init; }
}

/// <summary>Результат общей проверки Docker Compose перед массовой операцией.</summary>
public sealed record DockerPreflightResult(
    bool Success,
    IReadOnlySet<string> ComposeServices,
    IReadOnlyList<string> MissingServices,
    string? Error,
    string? Diagnostic);

/// <summary>Ошибка CLI с командой и обоими потоками вывода для диагностики в UI.</summary>
public sealed class DockerCommandException : Exception
{
    public DockerCommandException(string fileName, IReadOnlyList<string> arguments, int exitCode, string stdout, string stderr)
        : base(BuildMessage(fileName, arguments, exitCode, stderr))
    {
        FileName = fileName;
        Arguments = arguments;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
    }

    public string FileName { get; }
    public IReadOnlyList<string> Arguments { get; }
    public int ExitCode { get; }
    public string Stdout { get; }
    public string Stderr { get; }
    public string Command => $"{FileName} {string.Join(' ', Arguments)}";

    private static string BuildMessage(string fileName, IReadOnlyList<string> arguments, int exitCode, string stderr)
        => $"{fileName} {string.Join(' ', arguments)} → код {exitCode}: {stderr}".Trim();
}

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
public sealed class DockerService : IDockerDeployment
{
    private const string WebService = "web";
    private const string WebContainer = "cloud-web";

    // Управляемый набор: логическое имя UI/очереди -> имя сервиса Compose -> контейнер.
    // В compose BarkCloud ключи имеют префикс cloud-, а очередь работает с короткими именами.
    // Инфраструктуру не трогаем. Порядок важен для UpdateAll.
    private static readonly (string Service, string ComposeService, string Container)[] Managed =
    [
        ("configuration", "cloud-configuration", "cloud-configuration"),
        ("identity", "cloud-identity", "cloud-identity"),
        ("users", "cloud-users", "cloud-users"),
        ("files", "cloud-files", "cloud-files"),
        ("notification", "cloud-notification", "cloud-notification"),
        ("torrent", "cloud-torrent", "cloud-torrent"),
        (WebService, "cloud-web", WebContainer),
    ];

    // Пути внутри контейнера web, куда смонтированы compose-файл и .env (см. docker-compose.yml).
    private const string ComposeFileInContainer = "/docker-compose.yml";
    private const string EnvFileInContainer = "/.env";
    private const string MaintenanceDirectoryInContainer = "/app/maintenance";
    private const string MaintenanceVolumeKey = "cloud-web-maintenance";

    private readonly ILogger<DockerService> _logger;
    private readonly ComposeImageService _compose;
    private readonly MaintenanceOperationStore _maintenance;

    public DockerService(
        ILogger<DockerService> logger,
        ComposeImageService compose,
        MaintenanceOperationStore maintenance)
    {
        _logger = logger;
        _compose = compose;
        _maintenance = maintenance;
    }

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
                m.Service == WebService)
            {
                ComposeService = m.ComposeService,
            };
        }).ToList();

        if (dockerOk)
        {
            services = (await Task.WhenAll(services.Select(async service =>
                service with { ImageDigest = await GetContainerImageDigestAsync(service.Container, service.Image) }))).ToList();
        }

        return new ServicesSnapshot(services, dockerOk, error);
    }

    // ───────────────────────── Compose и health ─────────────────────────

    /// <summary>Скачать образ сервиса через Compose helper-контейнер.</summary>
    public async Task ComposePullAsync(string service)
    {
        if (!TryGetManagedDefinition(service, out var managed))
            throw new ArgumentException($"Неизвестный или недоступный для Compose сервис: {service}", nameof(service));
        await RunDockerComposeCommandAsync("pull", managed.ComposeService);
        _logger.LogInformation("Образ сервиса {Service} скачан", managed.Service);
    }

    /// <summary>Пересоздать сервис, не затрагивая его зависимости.</summary>
    public async Task ComposeUpAsync(string service)
    {
        if (!TryGetManagedNonWebService(service, out var canonical))
            throw new ArgumentException($"Неизвестный или недоступный для Compose сервис: {service}", nameof(service));
        await RunDockerComposeCommandAsync("up", "--force-recreate", "--no-deps", "-d", ComposeServiceOf(canonical));
        _logger.LogInformation("Контейнер сервиса {Service} пересоздан", canonical);
    }

    /// <summary>Удалить неиспользуемые образы после завершения всей задачи.</summary>
    public Task PruneImagesAsync() => RunDockerCommandAsync("image", "prune", "-f");

    /// <summary>Состояние контейнера и статус Docker healthcheck.</summary>
    public async Task<(string State, string Health)> InspectStateAsync(string container)
    {
        var output = await RunDockerCommandAsync("inspect", "--format",
            "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}", container);
        var parts = output.Split('|');
        return (parts[0], parts.Length > 1 ? parts[1] : "none");
    }

    /// <summary>Получить ID образа контейнера; null, если контейнер отсутствует.</summary>
    public async Task<string?> GetContainerImageIdAsync(string container)
    {
        try { return await RunDockerCommandAsync("inspect", "--format", "{{.Image}}", container); }
        catch { return null; }
    }

    /// <summary>Получить ссылку на образ контейнера; null, если контейнер отсутствует.</summary>
    public async Task<string?> GetContainerImageReferenceAsync(string container)
    {
        try { return await RunDockerCommandAsync("inspect", "--format", "{{.Config.Image}}", container); }
        catch { return null; }
    }

    /// <summary>Digest конкретного образа контейнера для сопоставления latest с SemVer.</summary>
    public async Task<string?> GetContainerImageDigestAsync(string container, string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;

        try
        {
            var imageId = await GetContainerImageIdAsync(container);
            if (string.IsNullOrWhiteSpace(imageId)) return null;

            var digests = await RunDockerCommandAsync(
                "image", "inspect", "--format", "{{join .RepoDigests \"\\n\"}}", imageId);
            var at = image.LastIndexOf('@');
            var repository = at >= 0
                ? image[..at]
                : image.LastIndexOf(':') > image.LastIndexOf('/')
                    ? image[..image.LastIndexOf(':')]
                    : image;
            var repositoryDigest = digests.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith(repository + "@", StringComparison.OrdinalIgnoreCase));
            // Образы, созданные через self-update или сохранённые локальным Docker,
            // иногда не содержат RepoDigests. В этом случае возвращаем config ID:
            // Registry-манифест также содержит этот digest в поле config.digest.
            return repositoryDigest ?? imageId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось получить digest контейнера {Container}", container);
            return null;
        }
    }

    /// <summary>
    /// Проверить Compose один раз и, для update, скачать все целевые образы одной командой.
    /// Возвращает отсутствующие optional-сервисы отдельно: это не ошибка массовой задачи.
    /// </summary>
    public async Task<DockerPreflightResult> PreflightAsync(
        IEnumerable<string> services,
        bool pullImages,
        CancellationToken cancellationToken = default)
    {
        var requested = services
            .Select(service => service.Trim())
            .Where(service => service.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            await RunDockerCommandAsync("info", "--format", "{{.ServerVersion}}");
            await GetComposeProjectAsync(requireLabel: true);
            await RunDockerComposeCommandAsync("version");
            var configured = (await RunDockerComposeCommandAsync("config", "--services"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(service => service.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var composeTargets = requested
                .Where(service => TryGetManagedDefinition(service, out var managed) && configured.Contains(managed.ComposeService))
                .Select(service => ComposeServiceOf(service))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var missing = requested
                .Where(service => !TryGetManagedDefinition(service, out var managed) || !configured.Contains(managed.ComposeService))
                .ToList();

            if (composeTargets.Count == 0)
            {
                // Очередь отдельно превратит отсутствующие обязательные сервисы в ошибку.
                // Если отсутствуют только optional-сервисы, корректный результат — все шаги Skipped,
                // а не повторная ошибка preflight без единой Docker-операции.
                return new DockerPreflightResult(
                    true,
                    configured,
                    missing,
                    null,
                    null);
            }

            if (pullImages)
                await RunDockerComposeCommandAsync(["pull", "--quiet", .. composeTargets]);
            return new DockerPreflightResult(true, configured, missing, null, null);
        }
        catch (Exception ex)
        {
            var diagnostic = FormatDiagnostic(ex);
            _logger.LogError(ex, "Общий Docker Compose preflight завершился ошибкой");
            return new DockerPreflightResult(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), [],
                "Общая проверка Docker Compose не пройдена", diagnostic);
        }
    }

    /// <summary>Получить текущую ссылку образа приложения из Compose-файла.</summary>
    public Task<string?> GetComposeImageReferenceAsync(string service)
        => _compose.GetImageReferenceAsync(ComposeServiceOf(service));

    /// <summary>Вернуть старый ID образа под его прежнюю ссылку для отката.</summary>
    public Task TagImageAsync(string imageId, string reference)
        => RunDockerCommandAsync("tag", imageId, reference);

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

    /// <summary>
    /// Обновить сам веб: detached helper тянет новый образ и пересоздаёт контейнер web
    /// «клонированием» его текущей конфигурации (mounts/env/ports/network/labels) из
    /// <c>docker inspect</c>. Compose здесь не используется намеренно — он требует разрешения
    /// относительных путей web (<c>./docker-compose.yml</c>) в реальные хостовые, что невозможно
    /// из Linux-helper'а на Windows-путях (<c>C:\…</c>). Клон же переиспользует те же источники
    /// mount'ов, что демон уже применяет, поэтому работает и на Windows Docker Desktop, и под
    /// Linux/WSL. При сбое пересоздания helper откатывается на прежний контейнер.
    /// </summary>
    public async Task<ServiceActionResult> UpdateWebSelfAsync(string? targetImage = null, string? operationId = null)
    {
        var normalizedOperationId = NormalizeOperationId(operationId);
        try
        {
            var spec = await BuildWebRecreateSpecAsync(targetImage);
            return await RunWebHelperAsync(
                "cloud-web-updater",
                BuildSelfUpdateScript(
                    spec,
                    normalizedOperationId,
                    _maintenance.StateFilePath,
                    _maintenance.LogFilePath,
                    _compose.BackupDirectory),
                "Обновление веб-клиента запущено",
                normalizedOperationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка подготовки самообновления веб-клиента");
            return new ServiceActionResult(false, "Не удалось подготовить обновление веб-клиента", ex.Message, normalizedOperationId);
        }
    }

    /// <summary>Перезапустить сам веб через detached helper-контейнер.</summary>
    public Task<ServiceActionResult> RestartWebSelfAsync(string? operationId = null)
    {
        var normalizedOperationId = NormalizeOperationId(operationId);
        return RunWebHelperAsync(
            "cloud-web-restarter",
            BuildRestartScript(
                normalizedOperationId,
                _maintenance.StateFilePath,
                _maintenance.LogFilePath),
            "Перезапуск веб-клиента запущен",
            normalizedOperationId);
    }

    /// <summary>Аргументы `docker run` для нового и rollback-контейнера web плюс доп. сети.</summary>
    private sealed record WebRecreateSpec(
        string Image,
        List<string> RunArgs,
        List<string> RollbackRunArgs,
        List<string> ExtraNetworkConnects,
        bool LegacyMaintenance);

    /// <summary>Собрать спецификацию пересоздания web из его текущего <c>docker inspect</c>.</summary>
    private async Task<WebRecreateSpec> BuildWebRecreateSpecAsync(string? targetImage)
    {
        var json = await RunDockerCommandAsync("inspect", WebContainer);
        using var doc = JsonDocument.Parse(json);
        var c = doc.RootElement[0];
        var config = c.GetProperty("Config");
        var host = c.GetProperty("HostConfig");

        var image = string.IsNullOrWhiteSpace(targetImage)
            ? config.GetProperty("Image").GetString()!
            : targetImage;
        var oldImageId = c.TryGetProperty("Image", out var imageIdProperty)
            ? imageIdProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(oldImageId))
            throw new InvalidOperationException("У контейнера cloud-web не удалось определить ID прежнего образа.");
        var id = c.TryGetProperty("Id", out var idp) ? idp.GetString() ?? "" : "";
        var shortId = id.Length >= 12 ? id[..12] : id;

        // без ведущего "run" — его добавляет шаблон скрипта (`docker run {args}`)
        var args = new List<string> { "-d", "--name", WebContainer };

        // restart policy
        if (host.TryGetProperty("RestartPolicy", out var rp) &&
            rp.TryGetProperty("Name", out var rpn) && rpn.GetString() is { Length: > 0 } policy && policy != "no")
        {
            var max = rp.TryGetProperty("MaximumRetryCount", out var mr) ? mr.GetInt32() : 0;
            args.Add("--restart");
            args.Add(policy == "on-failure" && max > 0 ? $"{policy}:{max}" : policy);
        }

        // user
        if (config.TryGetProperty("User", out var u) && u.GetString() is { Length: > 0 } user)
        {
            args.Add("--user");
            args.Add(user);
        }

        // env — полный набор (image-defaults + всё, что подставил compose из .env/environment)
        if (config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array)
            foreach (var e in env.EnumerateArray())
                if (e.GetString() is { } ev) { args.Add("-e"); args.Add(ev); }

        // labels — включая com.docker.compose.*, иначе сломается определение проекта при апдейте остальных
        if (config.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            foreach (var l in labels.EnumerateObject())
            {
                args.Add("--label");
                args.Add($"{l.Name}={l.Value.GetString()}");
            }

        // публикуемые порты
        if (host.TryGetProperty("PortBindings", out var pb) && pb.ValueKind == JsonValueKind.Object)
            foreach (var p in pb.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in p.Value.EnumerateArray())
                {
                    var hPort = b.TryGetProperty("HostPort", out var hp) ? hp.GetString() : null;
                    if (string.IsNullOrEmpty(hPort)) continue;
                    var hIp = b.TryGetProperty("HostIp", out var hi) ? hi.GetString() : null;
                    args.Add("-p");
                    args.Add(string.IsNullOrEmpty(hIp) ? $"{hPort}:{p.Name}" : $"{hIp}:{hPort}:{p.Name}");
                }
            }

        // mounts (bind + volume) — переиспользуем источники, которые демон уже знает (включая Windows-пути)
        var hasMaintenanceMount = false;
        if (c.TryGetProperty("Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
            foreach (var m in mounts.EnumerateArray())
            {
                var type = m.TryGetProperty("Type", out var tp) ? tp.GetString() : null;
                var dest = m.TryGetProperty("Destination", out var dp) ? dp.GetString() : null;
                var src = type == "volume"
                    ? (m.TryGetProperty("Name", out var nm) ? nm.GetString() : null)
                    : (m.TryGetProperty("Source", out var sp) ? sp.GetString() : null);
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dest)) continue;
                var spec = $"type={type},source={src},destination={dest}";
                if (dest == MaintenanceDirectoryInContainer)
                    hasMaintenanceMount = true;
                var readWrite = dest is ComposeFileInContainer or MaintenanceDirectoryInContainer
                    || m.TryGetProperty("RW", out var rwp) && rwp.GetBoolean();
                if (!readWrite) spec += ",readonly";
                args.Add("--mount");
                args.Add(spec);
            }

        // Старые установки не имели maintenance volume. Подключаем тот же Compose volume
        // по имени проекта и к helper, и к новому web, чтобы первый self-update уже был
        // способен сохранить marker/backup.
        if (!hasMaintenanceMount)
        {
            var project = await GetComposeProjectAsync();
            args.Add("--mount");
            args.Add($"type=volume,source={project}_{MaintenanceVolumeKey},destination={MaintenanceDirectoryInContainer}");
        }

        // сети: первую — на `docker run`, остальные — отдельным `docker network connect` после запуска
        var connects = new List<string>();
        if (c.TryGetProperty("NetworkSettings", out var ns) &&
            ns.TryGetProperty("Networks", out var nw) && nw.ValueKind == JsonValueKind.Object)
        {
            var first = true;
            foreach (var n in nw.EnumerateObject())
            {
                var aliases = new List<string>();
                if (n.Value.TryGetProperty("Aliases", out var al) && al.ValueKind == JsonValueKind.Array)
                    foreach (var a in al.EnumerateArray())
                        if (a.GetString() is { Length: > 0 } av && av != shortId) aliases.Add(av);

                if (first)
                {
                    args.Add("--network");
                    args.Add(n.Name);
                    foreach (var a in aliases) { args.Add("--network-alias"); args.Add(a); }
                    first = false;
                }
                else
                {
                    var parts = new List<string> { "docker", "network", "connect" };
                    foreach (var a in aliases) { parts.Add("--alias"); parts.Add(a); }
                    parts.Add(n.Name);
                    parts.Add(WebContainer);
                    connects.Add(string.Join(" ", parts.Select(ShQuote)));
                }
            }
        }

        args.Add(image); // новый образ — последним аргументом
        var rollbackArgs = args[..^1];
        rollbackArgs.Add(oldImageId); // ID сохраняет старый образ после обновления тега latest
        return new WebRecreateSpec(image, args, rollbackArgs, connects, !hasMaintenanceMount);
    }

    /// <summary>
    /// Скрипт для helper'а: тянет образ, под именем <c>-bak</c> гасит текущий web и поднимает новый;
    /// при сбое пересоздания — откат на прежний контейнер, чтобы веб не остался недоступным.
    /// </summary>
    private static string BuildSelfUpdateScript(
        WebRecreateSpec spec,
        string operationId,
        string stateFile,
        string logFile,
        string composeBackupDirectory)
    {
        var run = string.Join(" ", spec.RunArgs.Select(ShQuote));
        var rollbackRun = string.Join(" ", spec.RollbackRunArgs.Select(ShQuote));
        var connects = spec.ExtraNetworkConnects.Count == 0
            ? "true"
            : string.Join(" && ", spec.ExtraNetworkConnects.Select(cmd => $"run_logged {cmd}"));
        var stateWriter = BuildStateWriter(operationId, "update", stateFile, logFile);
        var composeBackup = Path.Combine(composeBackupDirectory, $"docker-compose-operation-{operationId}.yml");
        const string inspectFormat = "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}";
        var exposeLegacyState = spec.LegacyMaintenance
            ? $"  run_logged docker exec {WebContainer} sh -c {ShQuote($"mkdir -p {MaintenanceDirectoryInContainer}")} || true\n"
              + $"  run_logged docker cp {ShQuote(stateFile)} {ShQuote($"{WebContainer}:{stateFile}")} || true\n"
              + $"  run_logged docker cp {ShQuote(logFile)} {ShQuote($"{WebContainer}:{logFile}")} || true\n"
            : "  true\n";
        return
$"{stateWriter}\n" +
        "describe_command() {\n" +
        "  if [ \"$1\" = docker ] && [ \"$2\" = run ]; then\n" +
        "    printf '$ docker run <web-container-configuration>'\n" +
        "  else\n" +
        "    printf '$'\n" +
        "    printf ' %s' \"$@\"\n" +
        "  fi\n" +
        "}\n" +
        $"run_logged() {{ describe_command \"$@\" >> {ShQuote(logFile)}; printf '\\n' >> {ShQuote(logFile)}; \"$@\" >> {ShQuote(logFile)} 2>&1; code=$?; printf '[exit %s]\\n' \"$code\" >> {ShQuote(logFile)}; return \"$code\"; }}\n" +
        "expose_legacy_state() {\n" +
        exposeLegacyState +
        "}\n" +
        "wait_for_web() {\n" +
        "  i=0\n" +
        "  saw_running=0\n" +
        "  while [ \"$i\" -lt 60 ]; do\n" +
        $"    info=\"$(docker inspect --format {ShQuote(inspectFormat)} {WebContainer} 2>> {ShQuote(logFile)} || true)\n" +
        "    state=\"${info%%|*}\"\n" +
        "    health=\"${info#*|}\"\n" +
        "    if [ \"$state\" = running ] && [ \"$health\" = healthy ]; then return 0; fi\n" +
        "    if [ \"$state\" = running ] && [ \"$health\" = none ]; then\n" +
        "      if [ \"$saw_running\" = 1 ]; then return 0; fi\n" +
        "      saw_running=1\n" +
        "    fi\n" +
        "    case \"$state\" in exited|dead|restarting) return 1;; esac\n" +
        "    if [ \"$health\" = unhealthy ]; then return 1; fi\n" +
        "    i=$((i + 1))\n" +
        "    sleep 1\n" +
        "  done\n" +
        "  return 1\n" +
        "}\n" +
        $"restore_compose() {{ if [ -f {ShQuote(composeBackup)} ]; then cat {ShQuote(composeBackup)} > {ShQuote(ComposeFileInContainer)} 2>> {ShQuote(logFile)}; else return 0; fi; }}\n" +
        "rollback_web() {\n" +
        "  rollback_ok=1\n" +
        $"  if docker inspect {WebContainer} >/dev/null 2>&1; then if ! run_logged docker rm -f {WebContainer}; then rollback_ok=0; fi; fi\n" +
        $"  if ! run_logged docker rm -f {WebContainer}-bak; then rollback_ok=0; fi\n" +
        $"  if ! run_logged docker run {rollbackRun}; then rollback_ok=0; fi\n" +
        $"  if [ \"$rollback_ok\" = 1 ] && {connects}; then :; else rollback_ok=0; fi\n" +
        "  if ! restore_compose; then rollback_ok=0; fi\n" +
        "  if [ \"$rollback_ok\" = 1 ] && wait_for_web; then\n" +
        "    write_state failed 'Новый контейнер web не прошёл проверку; откат подтверждён'\n" +
        "  else\n" +
        "    write_state failed 'Новый контейнер web не прошёл проверку; откат не подтверждён'\n" +
        "  fi\n" +
        "  exit 1\n" +
        "}\n" +
        "write_state pending 'Операция запущена'\n" +
        "sleep 2\n" +
        $"if ! run_logged docker pull {ShQuote(spec.Image)}; then\n" +
        "  restore_compose || true\n" +
        "  write_state failed 'Не удалось скачать новый образ'\n" +
        "  expose_legacy_state\n" +
        "  exit 1\n" +
        "fi\n" +
        $"run_logged docker rm -f {WebContainer}-bak || true\n" +
        $"if ! run_logged docker rename {WebContainer} {WebContainer}-bak; then\n" +
        "  restore_compose || true\n" +
        "  write_state failed 'Не удалось сохранить старый контейнер'\n" +
        "  expose_legacy_state\n" +
        "  exit 1\n" +
        "fi\n" +
        $"run_logged docker stop -t 10 {WebContainer}-bak || true\n" +
        $"if run_logged docker run {run} && {connects} && wait_for_web; then\n" +
        $"  write_state completed 'Новый контейнер web запущен и прошёл проверку'\n" +
        $"  run_logged docker rm -f {WebContainer}-bak || true\n" +
        "  run_logged docker image prune -f || true\n" +
        "  exit 0\n" +
        "fi\n" +
        $"run_logged docker logs --tail 100 {WebContainer} || true\n" +
        "rollback_web";
    }

    private static string BuildRestartScript(string operationId, string stateFile, string logFile)
        => BuildStateWriter(operationId, "restart", stateFile, logFile) + "\n"
            + "describe_command() { printf '$'; printf ' %s' \"$@\"; }\n"
            + $"run_logged() {{ describe_command \"$@\" >> {ShQuote(logFile)}; printf '\\n' >> {ShQuote(logFile)}; \"$@\" >> {ShQuote(logFile)} 2>&1; code=$?; printf '[exit %s]\\n' \"$code\" >> {ShQuote(logFile)}; return \"$code\"; }}\n"
            + "write_state pending 'Перезапуск запущен'\n"
            + "sleep 2\n"
            + $"if run_logged docker restart {WebContainer}; then\n"
            + "  write_state completed 'Контейнер web перезапущен'\n"
            + "  exit 0\n"
            + "fi\n"
            + "write_state failed 'Не удалось перезапустить контейнер web'\n"
            + "exit 1";

    private static string BuildStateWriter(string operationId, string kind, string stateFile, string logFile)
    {
        var operationLiteral = JsonSerializer.Serialize(operationId);
        var kindLiteral = JsonSerializer.Serialize(kind);
        var diagnosticLiteral = JsonSerializer.Serialize($"Лог helper: {logFile}");
        return $"state_file={ShQuote(stateFile)}\n"
            + $"log_file={ShQuote(logFile)}\n"
            + $"diagnostic_json={ShQuote(diagnosticLiteral)}\n"
            + "mkdir -p \"$(dirname \"$state_file\")\" >/dev/null 2>&1 || true\n"
            + "mkdir -p \"$(dirname \"$log_file\")\" >/dev/null 2>&1 || true\n"
            + ": > \"$log_file\" 2>/dev/null || true\n"
            + "write_state() {\n"
            + "  tmp=\"${state_file}.tmp\"\n"
            + $"  printf '{{\"operationId\":{operationLiteral},\"kind\":{kindLiteral},\"state\":\"%s\",\"message\":\"%s\",\"diagnostic\":%s,\"updatedAtUtc\":\"%s\"}}\\n' \"$1\" \"$2\" \"$diagnostic_json\" \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\" > \"$tmp\" && mv -f \"$tmp\" \"$state_file\" || true\n"
            + "}";
    }

    private static string NormalizeOperationId(string? operationId)
        => Guid.TryParse(operationId, out var parsed)
            ? parsed.ToString("N")
            : Guid.NewGuid().ToString("N");

    /// <summary>Безопасное single-quote экранирование аргумента для <c>sh -c</c>.</summary>
    private static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Запустить detached helper-контейнер из образа web с готовым sh-скриптом.</summary>
    private async Task<ServiceActionResult> RunWebHelperAsync(
        string helperName,
        string innerScript,
        string startedMessage,
        string operationId)
    {
        try
        {
            var image = (await RunDockerCommandAsync("inspect", "--format", "{{.Config.Image}}", WebContainer)).Trim();
            var dockerSock = await GetMountSourceAsync(WebContainer, "/var/run/docker.sock");
            var maintenanceMount = await GetMaintenanceMountAsync();

            await TryRemoveContainerAsync(helperName);

            // Registry публичный на pull → креды не нужны; пустой DOCKER_CONFIG, чтобы CLI не звал
            // отсутствующий в alpine docker-credential-desktop из config.json хоста.
            string[] args =
            [
                "run", "-d", "--rm", "--name", helperName, "--user", "root",
                "-e", "DOCKER_HOST=unix:///var/run/docker.sock",
                "-e", "DOCKER_CONFIG=/tmp/barkcloud-docker",
                "-v", $"{dockerSock}:/var/run/docker.sock",
                "--entrypoint", "sh", image, "-c", innerScript,
            ];

            if (maintenanceMount is not null)
            {
                var insertAt = args.Length - 5;
                var withMaintenance = args.ToList();
                withMaintenance.Insert(insertAt, "--mount");
                withMaintenance.Insert(insertAt + 1, maintenanceMount);
                args = withMaintenance.ToArray();
            }

            await RunDockerCommandAsync(args);
            return new ServiceActionResult(true, startedMessage, null, operationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка запуска helper-контейнера {Helper}", helperName);
            return new ServiceActionResult(false, "Не удалось запустить операцию над веб-клиентом", ex.Message, operationId);
        }
    }

    // ───────────────────────── Внутреннее ─────────────────────────

    public static string ContainerNameFor(string service)
        => Managed.First(m => string.Equals(m.Service, service, StringComparison.OrdinalIgnoreCase)).Container;

    public static string ComposeServiceNameFor(string service)
        => ComposeServiceOf(service);

    private static string ComposeServiceOf(string service)
        => Managed.First(m => string.Equals(m.Service, service, StringComparison.OrdinalIgnoreCase)).ComposeService;

    private static string ContainerOf(string service) => ContainerNameFor(service);

    private static bool TryGetManagedDefinition(
        string service,
        out (string Service, string ComposeService, string Container) managed)
    {
        var match = Managed.FirstOrDefault(m => string.Equals(m.Service, service, StringComparison.OrdinalIgnoreCase));
        if (match.Service is null)
        {
            managed = default;
            return false;
        }

        managed = match;
        return true;
    }

    /// <summary>Проверить любое управляемое приложение сервисное имя, включая web.</summary>
    public static bool TryGetManagedService(string service, out string canonicalService)
    {
        var match = Managed.FirstOrDefault(item =>
            string.Equals(item.Service, service, StringComparison.OrdinalIgnoreCase));
        canonicalService = match.Service ?? string.Empty;
        return canonicalService.Length > 0;
    }

    /// <summary>Преобразовать имя Compose-сервиса в короткое имя очереди.</summary>
    public static string? LogicalServiceNameForCompose(string composeService)
        => Managed.FirstOrDefault(item =>
            string.Equals(item.ComposeService, composeService, StringComparison.OrdinalIgnoreCase)).Service;

    private static string FormatDiagnostic(Exception exception)
    {
        var command = exception as DockerCommandException
            ?? exception.InnerException as DockerCommandException;
        if (command is null)
            return LimitDiagnostic(exception.Message);

        var output = $"Команда: {command.Command}\nКод: {command.ExitCode}";
        if (!string.IsNullOrWhiteSpace(command.Stdout))
            output += $"\nstdout:\n{command.Stdout.Trim()}";
        if (!string.IsNullOrWhiteSpace(command.Stderr))
            output += $"\nstderr:\n{command.Stderr.Trim()}";
        return LimitDiagnostic(output);
    }

    private static string LimitDiagnostic(string text)
        => text.Length <= 8000 ? text : text[..8000] + "\n…";

    /// <summary>Проверить и канонизировать имя управляемого не-web сервиса.</summary>
    public static bool TryGetManagedNonWebService(string service, out string canonicalService)
    {
        var match = Managed.FirstOrDefault(m =>
            m.Service != WebService && string.Equals(m.Service, service, StringComparison.OrdinalIgnoreCase));
        canonicalService = match.Service ?? string.Empty;
        return canonicalService.Length > 0;
    }

    public static bool IsManagedNonWebService(string service)
        => TryGetManagedNonWebService(service, out _);

    /// <summary>Сервис из белого списка и не web (web — только self-методы).</summary>
    private static bool IsManagedNonWeb(string service, out ServiceActionResult? error)
    {
        if (!TryGetManagedNonWebService(service, out _))
        {
            if (string.Equals(service, WebService, StringComparison.OrdinalIgnoreCase))
            {
                error = new ServiceActionResult(false, "Веб-клиент управляется только через self-update / self-restart");
                return false;
            }

            error = new ServiceActionResult(false, $"Неизвестный сервис: {service}");
            return false;
        }

        error = null;
        return error is null;
    }

    /// <summary>Имя docker compose проекта берём из метки запущенного web-контейнера (без хардкода).</summary>
    private async Task<string> GetComposeProjectAsync(bool requireLabel = false)
    {
        try
        {
            var project = (await RunDockerCommandAsync(
                "inspect", "--format", "{{ index .Config.Labels \"com.docker.compose.project\" }}", WebContainer)).Trim();
            if (!string.IsNullOrEmpty(project)) return project;
        }
        catch (Exception ex)
        {
            if (requireLabel)
                throw new InvalidOperationException("Не удалось проверить имя Compose-проекта по метке cloud-web", ex);
            _logger.LogWarning(ex, "Не удалось определить имя compose-проекта, fallback=barkcloud");
        }

        if (requireLabel)
            throw new InvalidOperationException("У контейнера cloud-web отсутствует метка Compose-проекта");
        return "barkcloud";
    }

    /// <summary>Host-путь bind mount'а по destination внутри контейнера ("" если не найден).</summary>
    private async Task<string> GetMountSourceAsync(string container, string destination)
    {
        var template = "{{range .Mounts}}{{if eq .Destination \"" + destination + "\"}}{{.Source}}{{end}}{{end}}";
        return (await RunDockerCommandAsync("inspect", "--format", template, container)).Trim();
    }

    /// <summary>Получить mount-спеку maintenance volume без раскрытия внутреннего host-path volume.</summary>
    private async Task<string?> GetMaintenanceMountAsync()
    {
        try
        {
            var template = "{{range .Mounts}}{{if eq .Destination \"" + MaintenanceDirectoryInContainer
                + "\"}}{{.Type}}|{{.Source}}|{{.Name}}{{end}}{{end}}";
            var value = (await RunDockerCommandAsync("inspect", "--format", template, WebContainer)).Trim();
            var parts = value.Split('|');
            if (parts.Length < 3)
                return null;

            var type = parts[0];
            var source = parts[1];
            var name = parts[2];
            if (string.Equals(type, "volume", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(name))
                return $"type=volume,source={name},destination={MaintenanceDirectoryInContainer}";
            if (string.Equals(type, "bind", StringComparison.OrdinalIgnoreCase) && IsAbsoluteHostPath(source))
                return $"type=bind,src={source},dst={MaintenanceDirectoryInContainer}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось определить maintenance volume для helper-контейнера");
        }

        var project = await GetComposeProjectAsync();
        return $"type=volume,source={project}_{MaintenanceVolumeKey},destination={MaintenanceDirectoryInContainer}";
    }

    private async Task TryRemoveContainerAsync(string container)
    {
        try { await RunDockerCommandAsync("rm", "-f", container); }
        catch { /* контейнера нет — это нормально */ }
    }

    private Task<string> RunDockerCommandAsync(params string[] args) => RunProcessAsync("docker", args);

    /// <summary>
    /// Выполнить Compose через эфемерный helper-контейнер.
    ///
    /// Относительные bind mounts из compose-файла должны разрешаться Docker daemon'ом
    /// относительно реального каталога проекта на хосте. Поэтому helper получает
    /// этот каталог и файлы по тем host-путям, которые уже использованы web-контейнером,
    /// а не пытается запускать compose из своей рабочей директории.
    /// </summary>
    private async Task<string> RunDockerComposeCommandAsync(params string[] args)
    {
        var helperImage = (await RunDockerCommandAsync("inspect", "--format", "{{.Config.Image}}", WebContainer)).Trim();
        var dockerSocket = await GetMountSourceAsync(WebContainer, "/var/run/docker.sock");
        var composeFile = await GetMountSourceAsync(WebContainer, ComposeFileInContainer);
        var envFile = await GetMountSourceAsync(WebContainer, EnvFileInContainer);
        var projectDirectory = HostDirectoryOf(composeFile);

        if (string.IsNullOrWhiteSpace(helperImage)
            || string.IsNullOrWhiteSpace(dockerSocket)
            || string.IsNullOrWhiteSpace(composeFile)
            || string.IsNullOrWhiteSpace(envFile)
            || string.IsNullOrWhiteSpace(projectDirectory)
            || !IsAbsoluteHostPath(dockerSocket)
            || !IsAbsoluteHostPath(composeFile)
            || !IsAbsoluteHostPath(envFile)
            || !IsAbsoluteHostPath(projectDirectory))
        {
            throw new InvalidOperationException("Не удалось определить реальные host-пути Docker Compose.");
        }

        var project = await GetComposeProjectAsync();
        var dockerArguments = new List<string>
        {
            "run", "--rm", "--user", "root",
            "--mount", $"type=bind,src={dockerSocket},dst=/var/run/docker.sock",
            "--mount", $"type=bind,src={projectDirectory},dst={projectDirectory},readonly",
        };

        if (!string.Equals(HostDirectoryOf(envFile), projectDirectory, StringComparison.OrdinalIgnoreCase))
        {
            dockerArguments.Add("--mount");
            dockerArguments.Add($"type=bind,src={envFile},dst={envFile},readonly");
        }

        dockerArguments.Add("--entrypoint");
        dockerArguments.Add("docker");
        dockerArguments.Add(helperImage);
        dockerArguments.Add("compose");
        dockerArguments.Add("--project-name");
        dockerArguments.Add(project);
        dockerArguments.Add("--env-file");
        dockerArguments.Add(envFile);
        dockerArguments.Add("-f");
        dockerArguments.Add(composeFile);
        dockerArguments.AddRange(args);

        return await RunDockerCommandAsync(dockerArguments.ToArray());
    }

    private static bool IsAbsoluteHostPath(string path)
        => path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    /// <summary>Path.GetDirectoryName не распознаёт Windows-пути, когда web работает в Linux.</summary>
    private static string HostDirectoryOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var index = path.LastIndexOfAny(['/', '\\']);
        return index <= 0 ? (index == 0 ? path[..1] : string.Empty) : path[..index];
    }

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
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new DockerCommandException(fileName, args, process.ExitCode, output.ToString().Trim(), errors.ToString().Trim());

        return output.ToString().Trim();
    }
}
