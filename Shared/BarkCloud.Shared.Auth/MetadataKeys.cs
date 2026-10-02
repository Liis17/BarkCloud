namespace BarkCloud.Shared.Auth;

public class MetadataKeys
{
    public const string Token = "x-auth-token";

    public const string DeviceName = "x-device-name";

    public const string OsName = "x-os-name";

    public const string AppName = "x-app-name";

    public const string AppVersion = "x-app-version";

    public const string IpAddress = "x-ip-address";

    /// <summary>Доверенный адрес источника для лимитов попыток (nginx перезаписывает его; Web выставляет сам).</summary>
    public const string RealIp = "x-real-ip";

    public const string DeviceId = "x-device-id";
}