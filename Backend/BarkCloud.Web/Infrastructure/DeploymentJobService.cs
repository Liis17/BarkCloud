using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Серверная очередь Docker-операций для раздела «Обслуживание». Один consumer исключает
/// одновременные изменения Compose-стека. Перед update/restart выполняется единый preflight;
/// web обрабатывается последним через detached helper и требует переподключения браузера.
/// </summary>
public sealed class DeploymentJobService : BackgroundService
{
    /// <summary>Порядок обработки: configuration первым, web последним.</summary>
    public static readonly string[] DeployOrder =
    [
        "configuration",
        "identity",
        "users",
        "files",
        "notification",
        "torrent",
    ];

    public static readonly string[] FullDeployOrder = [.. DeployOrder, "web"];
    private static readonly HashSet<string> OptionalServices = ["notification", "torrent"];

    private readonly Channel<DeploymentJob> _queue = Channel.CreateUnbounded<DeploymentJob>();
    private readonly ConcurrentDictionary<Guid, DeploymentJob> _jobs = new();
    private readonly IDockerDeployment _docker;
    private readonly ComposeImageService _compose;
    private readonly DeploymentJobOptions _options;
    private readonly ILogger<DeploymentJobService> _logger;

    public DeploymentJobService(
        IDockerDeployment docker,
        ComposeImageService compose,
        DeploymentJobOptions options,
        ILogger<DeploymentJobService> logger)
    {
        _docker = docker;
        _compose = compose;
        _options = options;
        _logger = logger;
    }

    public DeploymentJob EnqueueUpdate(IEnumerable<string> services)
        => Enqueue(DeploymentJobKind.Update, services);

    public DeploymentJob EnqueueRestart(IEnumerable<string> services)
        => Enqueue(DeploymentJobKind.Restart, services);

    public DeploymentJob EnqueueStart(IEnumerable<string> services)
        => Enqueue(DeploymentJobKind.Start, services);

    public DeploymentJob EnqueueStop(IEnumerable<string> services)
        => Enqueue(DeploymentJobKind.Stop, services);

    public DeploymentJob EnqueueBranchSwitch(string service, string branch)
        => Enqueue(DeploymentJobKind.SwitchBranch, [service], branch);

    /// <summary>Массовая операция включает все application-сервисы и web последним.</summary>
    public Task<DeploymentJob> EnqueueAllAsync(DeploymentJobKind kind)
    {
        if (kind is not (DeploymentJobKind.Update or DeploymentJobKind.Restart))
            throw new ArgumentException("Массовая операция поддерживает только update и restart", nameof(kind));

        return Task.FromResult(Enqueue(kind, FullDeployOrder));
    }

    private DeploymentJob Enqueue(DeploymentJobKind kind, IEnumerable<string> services, string? branch = null)
    {
        var ordered = OrderServices(services);
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Steps = ordered.Select(service => new DeploymentStep { Service = service, Branch = branch }).ToList(),
            CreatedAtUtc = DateTime.UtcNow,
        };

        _jobs[job.Id] = job;
        TrimFinishedJobs();
        _queue.Writer.TryWrite(job);
        _logger.LogInformation("Задача обслуживания {JobId} ({Kind}) поставлена в очередь: {Services}",
            job.Id, kind, string.Join(", ", ordered));
        return job;
    }

    /// <summary>Сортировка по безопасному порядку с удалением дублей.</summary>
    private static IReadOnlyList<string> OrderServices(IEnumerable<string> services)
    {
        var list = services
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var known = FullDeployOrder.Where(s => list.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        known.AddRange(list.Where(s => !FullDeployOrder.Contains(s, StringComparer.OrdinalIgnoreCase)));
        return known;
    }

    public DeploymentJob? GetJob(Guid id)
        => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Активные задачи идут первыми, завершённые — от новых к старым.</summary>
    public IReadOnlyList<DeploymentJob> GetRecentJobs()
        => _jobs.Values
            .OrderByDescending(j => j.State is DeploymentJobState.Queued or DeploymentJobState.Running or DeploymentJobState.AwaitingReconnect)
            .ThenByDescending(j => j.CreatedAtUtc)
            .ToList();

    private void TrimFinishedJobs()
    {
        var keep = Math.Max(1, _options.MaxFinishedJobs);
        var stale = _jobs.Values
            .Where(job => job.State is DeploymentJobState.Completed or DeploymentJobState.Failed)
            .OrderByDescending(job => job.FinishedAtUtc ?? job.CreatedAtUtc)
            .Skip(keep)
            .ToList();

        foreach (var job in stale)
            _jobs.TryRemove(job.Id, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            await RunJobAsync(job, stoppingToken);
    }

    private async Task RunJobAsync(DeploymentJob job, CancellationToken ct)
    {
        job.State = DeploymentJobState.Running;
        job.StartedAtUtc = DateTime.UtcNow;
        _logger.LogInformation("Задача обслуживания {JobId} ({Kind}) запущена", job.Id, job.Kind);

        DockerPreflightResult? preflight = null;
        if (job.Kind is DeploymentJobKind.Update or DeploymentJobKind.Restart)
        {
            preflight = await _docker.PreflightAsync(
                job.Steps.Select(step => step.Service),
                job.Kind == DeploymentJobKind.Update,
                ct);

            if (!preflight.Success)
            {
                FailPreflight(job, preflight);
                return;
            }

            foreach (var step in job.Steps.Where(step => preflight.MissingServices.Contains(step.Service, StringComparer.OrdinalIgnoreCase)))
            {
                step.State = DeploymentStepState.Skipped;
                step.Message = "Сервис отсутствует в docker-compose.yml и не был затронут";
            }

            var missingRequired = preflight.MissingServices
                .Where(service => !OptionalServices.Contains(service))
                .ToList();
            if (missingRequired.Count > 0)
            {
                FailMissingRequired(job, missingRequired);
                return;
            }
        }

        var configurationFailed = false;
        var cancelled = false;

        foreach (var step in job.Steps)
        {
            if (step.State == DeploymentStepState.Skipped)
                continue;

            if (configurationFailed)
            {
                step.State = DeploymentStepState.Skipped;
                step.Message = "Пропущен: базовый сервис Configuration завершился с ошибкой";
                continue;
            }

            step.State = DeploymentStepState.InProgress;
            try
            {
                await RunStepAsync(job, step, preflight, ct);
                step.State = DeploymentStepState.Completed;

                if (job.RequiresReconnect)
                    break;
            }
            catch (OperationCanceledException)
            {
                step.State = DeploymentStepState.Failed;
                step.Message = "Операция остановлена при завершении веб-процесса";
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                step.State = DeploymentStepState.Failed;
                step.Message = ex.Message;
                step.Diagnostic = FormatDiagnostic(ex);
                if (string.Equals(step.Service, "configuration", StringComparison.OrdinalIgnoreCase))
                    configurationFailed = true;
                _logger.LogError(ex, "Задача {JobId}: шаг {Service} завершился ошибкой", job.Id, step.Service);
            }
        }

        if (job.RequiresReconnect)
        {
            job.State = DeploymentJobState.AwaitingReconnect;
            job.FinishedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Задача {JobId}: web-helper запущен, ожидается переподключение", job.Id);
            return;
        }

        var failed = job.Steps.Where(step => step.State == DeploymentStepState.Failed).ToList();
        if (!cancelled && failed.Count == 0 && job.Kind is DeploymentJobKind.Update or DeploymentJobKind.SwitchBranch)
        {
            try
            {
                await _docker.PruneImagesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Задача {JobId}: не удалось очистить неиспользуемые образы", job.Id);
            }
        }

        job.State = failed.Count > 0 || cancelled ? DeploymentJobState.Failed : DeploymentJobState.Completed;
        job.Error = failed.Count > 0
            ? string.Join("; ", failed.Select(step => $"{step.Service}: {step.Message}"))
            : cancelled ? "Операция остановлена" : null;
        job.Diagnostic = failed.Count == 1 ? failed[0].Diagnostic : null;
        job.FinishedAtUtc = DateTime.UtcNow;

        _logger.LogInformation("Задача обслуживания {JobId} завершена: {State}{Error}",
            job.Id, job.State, job.Error is null ? "" : $" — {job.Error}");
    }

    private static void FailPreflight(DeploymentJob job, DockerPreflightResult preflight)
    {
        job.State = DeploymentJobState.Failed;
        job.Error = preflight.Error ?? "Общая проверка Docker Compose не пройдена";
        job.Diagnostic = preflight.Diagnostic;
        foreach (var step in job.Steps.Where(step => step.State == DeploymentStepState.Pending))
        {
            step.State = DeploymentStepState.Skipped;
            step.Message = "Не выполнялся: общая проверка Docker Compose завершилась ошибкой";
        }
        job.FinishedAtUtc = DateTime.UtcNow;
    }

    private static void FailMissingRequired(DeploymentJob job, IReadOnlyList<string> services)
    {
        job.State = DeploymentJobState.Failed;
        job.Error = $"В docker-compose.yml отсутствуют обязательные сервисы: {string.Join(", ", services)}";
        foreach (var step in job.Steps.Where(step => step.State == DeploymentStepState.Pending))
        {
            step.State = DeploymentStepState.Skipped;
            step.Message = "Не выполнялся: в Compose отсутствует обязательный сервис";
        }
        job.FinishedAtUtc = DateTime.UtcNow;
    }

    private async Task RunStepAsync(
        DeploymentJob job,
        DeploymentStep step,
        DockerPreflightResult? preflight,
        CancellationToken ct)
    {
        if (job.Kind == DeploymentJobKind.SwitchBranch)
        {
            await BranchStepAsync(job, step, ct);
            return;
        }

        if (string.Equals(step.Service, "web", StringComparison.OrdinalIgnoreCase))
        {
            if (job.Kind is not (DeploymentJobKind.Update or DeploymentJobKind.Restart))
                throw new InvalidOperationException("Веб-клиент можно менять только через update/restart");

            var operationId = job.Id.ToString("N");
            var result = job.Kind == DeploymentJobKind.Update
                ? await _docker.UpdateWebSelfAsync(
                    await _docker.GetComposeImageReferenceAsync("web")
                        ?? throw new InvalidOperationException("В Compose не найден образ cloud-web"),
                    operationId)
                : await _docker.RestartWebSelfAsync(operationId);
            EnsureSuccess(result);
            step.Message = job.Kind == DeploymentJobKind.Update
                ? "Обновление web запущено через helper; выполняется переподключение"
                : "Перезапуск web запущен через helper; выполняется переподключение";
            job.RequiresReconnect = true;
            return;
        }

        if (!DockerService.TryGetManagedNonWebService(step.Service, out var canonicalService))
            throw new InvalidOperationException($"Неизвестный или недоступный для очереди сервис: {step.Service}");

        var container = DockerService.ContainerNameFor(canonicalService);
        switch (job.Kind)
        {
            case DeploymentJobKind.Start:
                if (await _docker.GetContainerImageIdAsync(container) is null)
                {
                    await _docker.ComposeUpAsync(canonicalService);
                    step.Message = "Создан и запущен";
                }
                else
                {
                    EnsureSuccess(await _docker.StartServiceAsync(canonicalService));
                    step.Message = "Запущен";
                }
                await EnsureHealthyAsync(container, ct);
                return;

            case DeploymentJobKind.Stop:
                EnsureSuccess(await _docker.StopServiceAsync(canonicalService));
                step.Message = "Остановлен";
                return;

            case DeploymentJobKind.Restart:
                if (await _docker.GetContainerImageIdAsync(container) is null)
                {
                    await _docker.ComposeUpAsync(canonicalService);
                    step.Message = "Создан и запущен";
                }
                else
                {
                    EnsureSuccess(await _docker.RestartServiceAsync(canonicalService));
                    step.Message = "Перезапущен";
                }
                await EnsureHealthyAsync(container, ct);
                return;

            case DeploymentJobKind.Update:
                await UpdateStepAsync(step, canonicalService, container, preflight?.Success == true, ct);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(job.Kind), job.Kind, "Неизвестный тип операции");
        }
    }

    private async Task UpdateStepAsync(
        DeploymentStep step,
        string service,
        string container,
        bool imagePrepared,
        CancellationToken ct)
    {
        var oldImageId = await _docker.GetContainerImageIdAsync(container);
        var oldReference = oldImageId is null ? null : await _docker.GetContainerImageReferenceAsync(container);
        var recreateAttempted = false;

        try
        {
            if (!imagePrepared)
                await _docker.ComposePullAsync(service);

            recreateAttempted = true;
            await _docker.ComposeUpAsync(service);
            var health = await WaitHealthyAsync(container, ct);
            if (health.Ok)
            {
                step.Message = "Обновлён, проверка состояния пройдена";
                return;
            }

            if (health.DefiniteFailure)
                await RollbackAsync(step, service, oldImageId, oldReference, health.Reason);

            throw new InvalidOperationException(health.Reason);
        }
        catch (Exception ex) when (recreateAttempted && ex is not OperationCanceledException && oldImageId is not null && oldReference is not null && !step.RolledBack)
        {
            await TryRollbackAsync(step, service, oldImageId, oldReference, ex.Message);
            throw new InvalidOperationException($"{ex.Message}. Выполнен откат на предыдущий образ", ex);
        }
    }

    private async Task BranchStepAsync(DeploymentJob job, DeploymentStep step, CancellationToken ct)
    {
        var branch = step.Branch ?? throw new InvalidOperationException("Канал не задан");
        var service = step.Service;
        var composeService = DockerService.ComposeServiceNameFor(service);
        var previousCompose = await _compose.SetBranchAsync(composeService, branch, job.Id.ToString("N"));
        var composeRestored = false;
        var container = DockerService.ContainerNameFor(service);
        var oldImageId = await _docker.GetContainerImageIdAsync(container);
        var oldReference = oldImageId is null ? null : await _docker.GetContainerImageReferenceAsync(container);

        try
        {
            await _docker.ComposePullAsync(service);
            if (string.Equals(service, "web", StringComparison.OrdinalIgnoreCase))
            {
                var targetImage = await _docker.GetComposeImageReferenceAsync(service)
                    ?? throw new InvalidOperationException("В Compose не найден образ cloud-web");
                EnsureSuccess(await _docker.UpdateWebSelfAsync(targetImage, job.Id.ToString("N")));
                step.Message = $"Канал {branch} применён, web-helper запущен";
                job.RequiresReconnect = true;
                return;
            }

            await _docker.ComposeUpAsync(service);
            var health = await WaitHealthyAsync(container, ct);
            if (health.Ok)
            {
                step.Message = $"Канал {branch} применён";
                return;
            }

            if (health.DefiniteFailure && oldImageId is not null && oldReference is not null)
            {
                await _docker.TagImageAsync(oldImageId, oldReference);
                await _compose.RestoreAsync(previousCompose);
                composeRestored = true;
                await _docker.ComposeUpAsync(service);
                step.RolledBack = true;
                throw new InvalidOperationException($"{health.Reason}. Канал и образ возвращены назад");
            }

            await _compose.RestoreAsync(previousCompose);
            composeRestored = true;
            throw new InvalidOperationException($"{health.Reason}. Compose-файл возвращён к прежнему каналу");
        }
        catch
        {
            if (!job.RequiresReconnect && !composeRestored)
                await _compose.RestoreAsync(previousCompose);
            throw;
        }
    }

    private async Task RollbackAsync(
        DeploymentStep step,
        string service,
        string? oldImageId,
        string? oldReference,
        string reason)
    {
        if (oldImageId is null || oldReference is null)
            throw new InvalidOperationException(reason);

        await _docker.TagImageAsync(oldImageId, oldReference);
        await _docker.ComposeUpAsync(service);
        step.RolledBack = true;
        throw new InvalidOperationException($"{reason}. Выполнен откат на предыдущий образ");
    }

    private async Task TryRollbackAsync(
        DeploymentStep step,
        string service,
        string oldImageId,
        string oldReference,
        string reason)
    {
        try
        {
            await _docker.TagImageAsync(oldImageId, oldReference);
            await _docker.ComposeUpAsync(service);
            step.RolledBack = true;
        }
        catch (Exception rollbackException)
        {
            throw new InvalidOperationException(
                $"{reason}. Не удалось откатить предыдущий образ: {rollbackException.Message}", rollbackException);
        }
    }

    private async Task EnsureHealthyAsync(string container, CancellationToken ct)
    {
        var health = await WaitHealthyAsync(container, ct);
        if (!health.Ok)
            throw new InvalidOperationException(health.Reason);
    }

    private static void EnsureSuccess(ServiceActionResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorDetails is null
                ? result.Message
                : $"{result.Message}: {result.ErrorDetails}");
    }

    private sealed record HealthResult(bool Ok, bool DefiniteFailure, string Reason);

    private async Task<HealthResult> WaitHealthyAsync(string container, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _options.HealthTimeout;
        var sawRunning = false;
        await Task.Delay(_options.InitialSettleDelay, ct);

        while (true)
        {
            string state;
            string health;
            try
            {
                (state, health) = await _docker.InspectStateAsync(container);
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow >= deadline)
                    return new HealthResult(false, false, $"Не удалось проверить состояние контейнера: {ex.Message}");

                await Task.Delay(_options.HealthPollInterval, ct);
                continue;
            }

            if (state is "exited" or "dead")
                return new HealthResult(false, true, $"Контейнер не запустился (state={state})");
            if (state == "restarting")
                return new HealthResult(false, true, "Контейнер падает и перезапускается (crash-loop)");
            if (health == "unhealthy")
                return new HealthResult(false, true, "Docker healthcheck: unhealthy");
            if (state == "running" && health == "healthy")
                return new HealthResult(true, false, string.Empty);
            if (state == "running" && health == "none")
            {
                if (sawRunning)
                    return new HealthResult(true, false, string.Empty);
                sawRunning = true;
            }

            if (DateTime.UtcNow >= deadline)
                return new HealthResult(false, false,
                    $"Контейнер не стал доступен за {_options.HealthTimeout.TotalSeconds:N0} с (state={state}, health={health})");

            await Task.Delay(_options.HealthPollInterval, ct);
        }
    }

    private static string FormatDiagnostic(Exception exception)
    {
        var command = exception as DockerCommandException
            ?? exception.InnerException as DockerCommandException;
        if (command is null)
            return exception.Message;

        var diagnostic = $"Команда: {command.Command}\nКод: {command.ExitCode}";
        if (!string.IsNullOrWhiteSpace(command.Stdout))
            diagnostic += $"\nstdout:\n{command.Stdout.Trim()}";
        if (!string.IsNullOrWhiteSpace(command.Stderr))
            diagnostic += $"\nstderr:\n{command.Stderr.Trim()}";
        return diagnostic.Length <= 8000 ? diagnostic : diagnostic[..8000] + "\n…";
    }
}
