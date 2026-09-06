using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Серверная очередь Docker-операций для раздела «Обслуживание».
/// Один потребитель гарантирует, что несколько compose-операций не меняют стек одновременно.
/// Каждый update/restart завершается проверкой состояния контейнера; при явном падении
/// обновление пытается вернуть предыдущий образ. Очистка образов выполняется только в конце
/// всей задачи, чтобы не уничтожить материал для отката.
/// </summary>
public sealed class DeploymentJobService : BackgroundService
{
    /// <summary>Порядок запуска: базовые сервисы идут раньше зависящих от них.</summary>
    public static readonly string[] DeployOrder =
    [
        "configuration",
        "identity",
        "users",
        "files",
        "notification",
        "torrent",
    ];

    private readonly Channel<DeploymentJob> _queue = Channel.CreateUnbounded<DeploymentJob>();
    private readonly ConcurrentDictionary<Guid, DeploymentJob> _jobs = new();
    private readonly IDockerDeployment _docker;
    private readonly DeploymentJobOptions _options;
    private readonly ILogger<DeploymentJobService> _logger;

    public DeploymentJobService(
        IDockerDeployment docker,
        DeploymentJobOptions options,
        ILogger<DeploymentJobService> logger)
    {
        _docker = docker;
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

    /// <summary>
    /// Поставить в очередь все реально существующие сервисы приложения.
    /// Отсутствующие optional-сервисы не превращают массовую операцию в ложную ошибку.
    /// </summary>
    public async Task<DeploymentJob> EnqueueAllAsync(DeploymentJobKind kind)
    {
        if (kind is not (DeploymentJobKind.Update or DeploymentJobKind.Restart))
            throw new ArgumentException("Массовая операция поддерживает только update и restart", nameof(kind));

        var snapshot = await _docker.GetServicesStatusAsync();
        if (!snapshot.DockerOk)
            throw new InvalidOperationException(snapshot.Error ?? "Docker недоступен");

        var services = snapshot.Services
            .Where(s => !s.IsWeb && s.State is not ("not_found" or "unavailable"))
            .Select(s => s.Service)
            .ToList();

        if (services.Count == 0)
            throw new InvalidOperationException("Не найдено запущенных или остановленных сервисов приложения");

        return Enqueue(kind, services);
    }

    private DeploymentJob Enqueue(DeploymentJobKind kind, IEnumerable<string> services)
    {
        var ordered = OrderServices(services);
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Steps = ordered.Select(service => new DeploymentStep { Service = service }).ToList(),
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

        var known = DeployOrder.Where(s => list.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        var unknown = list.Where(s => !DeployOrder.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        known.AddRange(unknown);
        return known;
    }

    public DeploymentJob? GetJob(Guid id)
        => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Активные задачи идут первыми, завершённые — от новых к старым.</summary>
    public IReadOnlyList<DeploymentJob> GetRecentJobs()
        => _jobs.Values
            .OrderByDescending(j => j.State is DeploymentJobState.Queued or DeploymentJobState.Running)
            .ThenByDescending(j => j.CreatedAtUtc)
            .ToList();

    private void TrimFinishedJobs()
    {
        var keep = Math.Max(1, _options.MaxFinishedJobs);
        var stale = _jobs.Values
            .Where(j => j.State is DeploymentJobState.Completed or DeploymentJobState.Failed)
            .OrderByDescending(j => j.FinishedAtUtc ?? j.CreatedAtUtc)
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

        var cancelled = false;
        foreach (var step in job.Steps)
        {
            step.State = DeploymentStepState.InProgress;
            try
            {
                await RunStepAsync(job.Kind, step, ct);
                step.State = DeploymentStepState.Completed;
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
                _logger.LogError(ex, "Задача {JobId}: шаг {Service} завершился ошибкой", job.Id, step.Service);
            }
        }

        if (!cancelled && job.Kind == DeploymentJobKind.Update)
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

        var failed = job.Steps.Where(s => s.State == DeploymentStepState.Failed).ToList();
        job.State = failed.Count > 0 || cancelled ? DeploymentJobState.Failed : DeploymentJobState.Completed;
        job.Error = failed.Count > 0
            ? string.Join("; ", failed.Select(s => $"{s.Service}: {s.Message}"))
            : cancelled ? "Операция остановлена" : null;
        job.FinishedAtUtc = DateTime.UtcNow;

        _logger.LogInformation("Задача обслуживания {JobId} завершена: {State}{Error}",
            job.Id, job.State, job.Error is null ? "" : $" — {job.Error}");
    }

    private async Task RunStepAsync(DeploymentJobKind kind, DeploymentStep step, CancellationToken ct)
    {
        if (!DockerService.TryGetManagedNonWebService(step.Service, out var canonicalService))
            throw new InvalidOperationException($"Неизвестный или недоступный для очереди сервис: {step.Service}");

        var container = DockerService.ContainerNameFor(canonicalService);
        switch (kind)
        {
            case DeploymentJobKind.Start:
                await EnsureSuccessAsync(_docker.StartServiceAsync(canonicalService));
                await EnsureHealthyAsync(container, ct);
                step.Message = "Запущен";
                return;

            case DeploymentJobKind.Stop:
                await EnsureSuccessAsync(_docker.StopServiceAsync(canonicalService));
                step.Message = "Остановлен";
                return;

            case DeploymentJobKind.Restart:
                await EnsureSuccessAsync(_docker.RestartServiceAsync(canonicalService));
                await EnsureHealthyAsync(container, ct);
                step.Message = "Перезапущен";
                return;

            case DeploymentJobKind.Update:
                await UpdateStepAsync(step, canonicalService, container, ct);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный тип операции");
        }
    }

    private async Task UpdateStepAsync(DeploymentStep step, string service, string container, CancellationToken ct)
    {
        // Старый ID сохраняем до pull/up: latest может указывать уже на другой образ.
        var oldImageId = await _docker.GetContainerImageIdAsync(container);
        var oldReference = oldImageId is null ? null : await _docker.GetContainerImageReferenceAsync(container);

        await _docker.ComposePullAsync(service);
        await _docker.ComposeUpAsync(service);

        var health = await WaitHealthyAsync(container, ct);
        if (health.Ok)
        {
            step.Message = "Обновлён, проверка состояния пройдена";
            return;
        }

        // Таймаут не доказывает поломку: сервис может запускаться дольше обычного.
        // При явном crash-loop/exited/unhealthy безопасно возвращаем старую ссылку.
        if (health.DefiniteFailure && oldImageId is not null && oldReference is not null)
        {
            try
            {
                await _docker.TagImageAsync(oldImageId, oldReference);
                await _docker.ComposeUpAsync(service);
                step.RolledBack = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{health.Reason}. Не удалось откатить предыдущий образ: {ex.Message}", ex);
            }

            throw new InvalidOperationException($"{health.Reason}. Выполнен откат на предыдущий образ");
        }

        throw new InvalidOperationException(health.Reason);
    }

    private async Task EnsureHealthyAsync(string container, CancellationToken ct)
    {
        var health = await WaitHealthyAsync(container, ct);
        if (!health.Ok)
            throw new InvalidOperationException(health.Reason);
    }

    private static async Task EnsureSuccessAsync(Task<ServiceActionResult> action)
    {
        var result = await action;
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorDetails is null
                ? result.Message
                : $"{result.Message}: {result.ErrorDetails}");
    }

    private sealed record HealthResult(bool Ok, bool DefiniteFailure, string Reason);

    /// <summary>
    /// Состояние running без собственного healthcheck считается успешным после двух
    /// последовательных опросов. Явный exited/dead/restarting/unhealthy — повод для отката.
    /// </summary>
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
}
