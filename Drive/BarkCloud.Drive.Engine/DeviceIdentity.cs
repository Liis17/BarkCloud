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
    {
        DeviceName = Environment.MachineName;
        OsName = RuntimeInformation.OSDescription;
        AppName = "BarkCloud.Drive";
        AppVersion = "0.1.0";
        DeviceId = LoadOrCreateDeviceId();
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
