using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace BarkCloud.Drive.Contracts;

[SupportedOSPlatform("windows")]
public static class EnginePipe
{
    public static string Name => $"BarkCloud.Drive.Engine.{WindowsIdentity.GetCurrent().User!.Value}";

    public const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
}
