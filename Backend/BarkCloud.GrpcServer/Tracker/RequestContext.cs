namespace BarkCloud.GrpcServer.Tracker;

public class RequestContext
{
    public string? OperationSystem { get; init; }

    public string? IpAddress { get; init; }

    /// <summary>Доверенный адрес источника (см. <see cref="SourceIpResolver"/>) — только для лимитов попыток.</summary>
    public string? SourceIp { get; init; }

    public string? DeviceName { get; init; }

    public string? AppName { get; init; }

    public string? AppVersion { get; init; }

    public string? DeviceId { get; init; }
}
