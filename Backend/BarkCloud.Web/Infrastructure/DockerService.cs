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

    // ───────────────────────── Compose и health ─────────────────────────

    /// <summary>Скачать образ сервиса через Compose helper-контейнер.</summary>
    public async Task ComposePullAsync(string service)
    {
        if (!TryGetManagedNonWebService(service, out var canonical))
            throw new ArgumentException($"Неизвестный или недоступный для Compose сервис: {service}", nameof(service));
        await RunDockerComposeCommandAsync("pull", ComposeServiceOf(canonical));
        _logger.LogInformation("Образ сервиса {Service} скачан", canonical);
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
    public async Task<ServiceActionResult> UpdateWebSelfAsync()
    {
        try
        {
            var spec = await BuildWebRecreateSpecAsync();
            return await RunWebHelperAsync("cloud-web-updater", BuildSelfUpdateScript(spec), "Обновление веб-клиента запущено");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка подготовки самообновления веб-клиента");
            return new ServiceActionResult(false, "Не удалось подготовить обновление веб-клиента", ex.Message);
        }
    }

    /// <summary>Перезапустить сам веб через detached helper-контейнер.</summary>
    public Task<ServiceActionResult> RestartWebSelfAsync()
        => RunWebHelperAsync("cloud-web-restarter", $"sleep 2 && docker restart {WebContainer}", "Перезапуск веб-клиента запущен");

    /// <summary>Образ + аргументы `docker run` для пересоздания web и команды подключения доп. сетей.</summary>
    private sealed record WebRecreateSpec(string Image, List<string> RunArgs, List<string> ExtraNetworkConnects);

    /// <summary>Собрать спецификацию пересоздания web из его текущего <c>docker inspect</c>.</summary>
    private async Task<WebRecreateSpec> BuildWebRecreateSpecAsync()
    {
        var json = await RunDockerCommandAsync("inspect", WebContainer);
        using var doc = JsonDocument.Parse(json);
        var c = doc.RootElement[0];
        var config = c.GetProperty("Config");
        var host = c.GetProperty("HostConfig");

        var image = config.GetProperty("Image").GetString()!;
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
                if (!(m.TryGetProperty("RW", out var rwp) && rwp.GetBoolean())) spec += ",readonly";
                args.Add("--mount");
                args.Add(spec);
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

        args.Add(image); // образ — последним аргументом
        return new WebRecreateSpec(image, args, connects);
    }

    /// <summary>
    /// Скрипт для helper'а: тянет образ, под именем <c>-bak</c> гасит текущий web и поднимает новый;
    /// при сбое пересоздания — откат на прежний контейнер, чтобы веб не остался недоступным.
    /// </summary>
    private static string BuildSelfUpdateScript(WebRecreateSpec spec)
    {
        var run = string.Join(" ", spec.RunArgs.Select(ShQuote));
        var connects = spec.ExtraNetworkConnects.Count == 0
            ? "true"
            : string.Join(" && ", spec.ExtraNetworkConnects.Select(cmd => $"{cmd} >/dev/null 2>&1"));
        return
$@"sleep 2
docker pull {ShQuote(spec.Image)} || exit 1
docker rm -f {WebContainer}-bak >/dev/null 2>&1 || true
docker rename {WebContainer} {WebContainer}-bak >/dev/null 2>&1 || exit 1
docker stop -t 10 {WebContainer}-bak >/dev/null 2>&1
if docker run {run} && {connects}; then
  docker rm -f {WebContainer}-bak >/dev/null 2>&1
  docker image prune -f >/dev/null 2>&1
else
  docker rm -f {WebContainer} >/dev/null 2>&1
  docker rename {WebContainer}-bak {WebContainer} >/dev/null 2>&1
  docker start {WebContainer} >/dev/null 2>&1
fi";
    }

    /// <summary>Безопасное single-quote экранирование аргумента для <c>sh -c</c>.</summary>
    private static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Запустить detached helper-контейнер из образа web с готовым sh-скриптом.</summary>
    private async Task<ServiceActionResult> RunWebHelperAsync(string helperName, string innerScript, string startedMessage)
    {
        try
        {
            var image = (await RunDockerCommandAsync("inspect", "--format", "{{.Config.Image}}", WebContainer)).Trim();
            var dockerSock = await GetMountSourceAsync(WebContainer, "/var/run/docker.sock");

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

            await RunDockerCommandAsync(args);
            return new ServiceActionResult(true, startedMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка запуска helper-контейнера {Helper}", helperName);
            return new ServiceActionResult(false, "Не удалось запустить операцию над веб-клиентом", ex.Message);
        }
    }

    // ───────────────────────── Внутреннее ─────────────────────────

    public static string ContainerNameFor(string service)
        => Managed.First(m => string.Equals(m.Service, service, StringComparison.OrdinalIgnoreCase)).Container;

    private static string ComposeServiceOf(string service)
        => Managed.First(m => string.Equals(m.Service, service, StringComparison.OrdinalIgnoreCase)).ComposeService;

    private static string ContainerOf(string service) => ContainerNameFor(service);

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

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} → код {process.ExitCode}: {errors}".Trim());

        return output.ToString().Trim();
    }
}
