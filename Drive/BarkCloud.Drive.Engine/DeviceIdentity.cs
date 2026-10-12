using System.Reflection;
using System.Runtime.InteropServices;

namespace BarkCloud.Drive.Engine;

// Идентичность устройства для device-метадаты (Auth её требует).
// DeviceId персистится, иначе каждый вход = новое устройство (refresh ищется по device_id).
internal sealed class DeviceIdentity
{
    public string DeviceId { get; }
    public string DeviceName { get; }
    public string OsName { get; }
    public string AppName { get; }
    public string AppVersion { get; }

    public DeviceIdentity()
        : this(LoadOrCreateDeviceId(), Environment.MachineName, RuntimeInformation.OSDescription, "BarkCloud.Drive")
    {
    }

    private static string ReadAssemblyInformationalVersion()
        => typeof(DeviceIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    internal DeviceIdentity(string deviceId, string deviceName, string osName, string appName, string? appVersion = null)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        OsName = osName;
        AppName = appName;
        AppVersion = appVersion ?? ReadAssemblyInformationalVersion();
    }

    private static string LoadOrCreateDeviceId()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BarkCloud.Drive");
        Directory.CreateDirectory(dir);

        var file = Path.Combine(dir, "device-id");
        if (File.Exists(file) && Guid.TryParse(File.ReadAllText(file).Trim(), out var existing))
            return existing.ToString();

        var id = Guid.NewGuid().ToString();
        File.WriteAllText(file, id);
        return id;
    }
}
