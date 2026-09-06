namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Операции Docker, необходимые серверной очереди обслуживания.
/// Отдельный публичный контракт позволяет проверять очередь без запуска Docker CLI.
/// </summary>
public interface IDockerDeployment
{
    Task<ServicesSnapshot> GetServicesStatusAsync();

    Task<ServiceActionResult> RestartServiceAsync(string service);
    Task<ServiceActionResult> StartServiceAsync(string service);
    Task<ServiceActionResult> StopServiceAsync(string service);

    Task ComposePullAsync(string service);
    Task ComposeUpAsync(string service);
    Task PruneImagesAsync();

    Task<(string State, string Health)> InspectStateAsync(string container);
    Task<string?> GetContainerImageIdAsync(string container);
    Task<string?> GetContainerImageReferenceAsync(string container);
    Task TagImageAsync(string imageId, string reference);
}
