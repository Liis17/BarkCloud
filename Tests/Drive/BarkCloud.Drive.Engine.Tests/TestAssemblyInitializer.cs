using System.Runtime.CompilerServices;

using BarkCloud.Drive.Engine;

namespace BarkCloud.Drive.Engine.Tests;

internal static class TestAssemblyInitializer
{
    internal static string LogDirectory => Path.Combine(
        Path.GetTempPath(), "BarkCloud.Drive.Engine.Tests", Environment.ProcessId.ToString());

    [ModuleInitializer]
    internal static void Initialize()
        => EngineLog.Configure(LogDirectory);
}
