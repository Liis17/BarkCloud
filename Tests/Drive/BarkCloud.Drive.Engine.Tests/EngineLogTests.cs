using BarkCloud.Drive.Engine;
using BarkCloud.Drive.Engine.Tests.Fakes;

using FluentAssertions;

namespace BarkCloud.Drive.Engine.Tests;

[CollectionDefinition(nameof(EngineLogCollection), DisableParallelization = true)]
public sealed class EngineLogCollection { }

[Collection(nameof(EngineLogCollection))]
public sealed class EngineLogTests
{
    [Fact]
    public void Log_larger_than_five_mib_is_rotated()
    {
        using var temp = new TestDirectory();
        var logFile = temp.Path("engine.log");
        var original = new string('x', 5 * 1024 * 1024 + 1);
        File.WriteAllText(logFile, original);
        EngineLog.Configure(temp.Root);

        try
        {
            EngineLog.Info("after rotation");

            File.ReadAllText(temp.Path("engine.log.1")).Should().Be(original);
            File.ReadAllText(logFile).Should().Contain("after rotation");
        }
        finally
        {
            EngineLog.Configure(TestAssemblyInitializer.LogDirectory);
        }
    }
    [Fact]
    public void Failed_rotation_still_appends_to_current_log()
    {
        using var temp = new TestDirectory();
        var logFile = temp.Path("engine.log");
        File.WriteAllText(logFile, new string('x', 5 * 1024 * 1024 + 1));
        EngineLog.Configure(temp.Root);

        try
        {
            using var locked = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            EngineLog.Info("rotation failed but append succeeded");

            File.Exists(temp.Path("engine.log.1")).Should().BeFalse();
            File.ReadAllText(logFile).Should().Contain("rotation failed but append succeeded");
        }
        finally
        {
            EngineLog.Configure(TestAssemblyInitializer.LogDirectory);
        }
    }

}
