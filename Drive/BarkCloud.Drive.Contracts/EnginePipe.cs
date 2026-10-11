using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace BarkCloud.Drive.Contracts;

[SupportedOSPlatform("windows")]
public static class EnginePipe
{
    private static readonly string UserSid = GetCurrentUserSid();

    public static string Name => $"BarkCloud.Drive.Engine.{UserSid}";

    public const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    private static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }
}
