using System.Reflection;

using BarkCloud.Drive.Engine;

using FluentAssertions;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class DeviceIdentityTests
{
    [Fact]
    public void App_version_comes_from_assembly_informational_version()
    {
        var expected = typeof(DeviceIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var identity = new DeviceIdentity("test-id", "test-device", "test-os", "test-app");

        identity.AppVersion.Should().Be(expected);
        identity.AppVersion.Should().NotContain("+");
    }
}
