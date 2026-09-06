namespace BarkCloud.Web.Infrastructure;

/// <summary>Состояние проверки версии образа в Docker Registry.</summary>
public static class ImageVersionState
{
    public const string Ready = "ready";
    public const string Unknown = "unknown";
    public const string RegistryUnavailable = "registry_unavailable";
}

/// <summary>Версия образа и результат сравнения с каналом в реестре.</summary>
public sealed record ImageVersionStatus
{
    public string? Repository { get; init; }
    public string? Tag { get; init; }
    public string? Branch { get; init; }
    public string? CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public bool? UpdateAvailable { get; init; }
    public string State { get; init; } = ImageVersionState.Unknown;
    public string? Error { get; init; }
}
