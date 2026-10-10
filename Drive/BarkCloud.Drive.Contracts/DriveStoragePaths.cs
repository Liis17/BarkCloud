namespace BarkCloud.Drive.Contracts;

public static class DriveStoragePaths
{
    public static string OutboxDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BarkCloud.Drive", "outbox");
}
