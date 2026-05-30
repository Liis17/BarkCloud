using System.IO;

namespace BarkCloud.Drive.App;

// Свободные буквы дисков D..Z (исключая занятые системой/смонтированные).
internal static class DriveLetters
{
    public static List<string> Free()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = new List<string>();
        for (var c = 'D'; c <= 'Z'; c++)
            if (!used.Contains(c))
                free.Add(c.ToString());
        return free;
    }
}
