using System.Runtime.CompilerServices;

using BarkCloud.Drive.Engine;

namespace BarkCloud.Drive.Engine.Tests;

internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
        => EngineLog.Configure(Path.Combine(
            Path.GetTempPath(), "BarkCloud.Drive.Engine.Tests", Environment.ProcessId.ToString()));
}
