using System.Text.Json.Serialization;

namespace BarkCloud.Web.Infrastructure;

/// <summary>Тип операции в серверной очереди обслуживания.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentJobKind
{
    Update,
    Restart,
    Start,
    Stop,
    SwitchBranch,
}

/// <summary>Состояние всей задачи обслуживания.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentJobState
{
    Queued,
    Running,
    AwaitingReconnect,
    Completed,
    Failed,
}

/// <summary>Состояние шага над одним сервисом.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentStepState
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,
}

/// <summary>Один последовательный шаг задачи обслуживания.</summary>
public sealed class DeploymentStep
{
    public string Service { get; init; } = string.Empty;

    public string? Branch { get; init; }

    public DeploymentStepState State { get; set; } = DeploymentStepState.Pending;

    public string? Message { get; set; }

    public string? Diagnostic { get; set; }

    public bool RolledBack { get; set; }
}

/// <summary>
/// Задача обслуживания. Экземпляр живёт в памяти веб-процесса и доступен UI для опроса.
/// </summary>
public sealed class DeploymentJob
{
    public Guid Id { get; init; }

    public DeploymentJobKind Kind { get; init; }

    public List<DeploymentStep> Steps { get; init; } = [];

    public DeploymentJobState State { get; set; } = DeploymentJobState.Queued;

    public string? Error { get; set; }

    public string? Diagnostic { get; set; }

    public bool RequiresReconnect { get; set; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>Настройки ожидания запуска контейнеров. Вынесены для быстрых детерминированных тестов.</summary>
public sealed class DeploymentJobOptions
{
    public TimeSpan InitialSettleDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxFinishedJobs { get; init; } = 20;
}
