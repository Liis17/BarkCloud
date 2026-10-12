using System.Diagnostics;
using System.IO;

using BarkCloud.Drive.Contracts;

namespace BarkCloud.Drive.App;

internal static class OutboxFolder
{
    public static void Open()
    {
        Directory.CreateDirectory(DriveStoragePaths.OutboxDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = DriveStoragePaths.OutboxDirectory,
            UseShellExecute = true,
        });
    }
}
