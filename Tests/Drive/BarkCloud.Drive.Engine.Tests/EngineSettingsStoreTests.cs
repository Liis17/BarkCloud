using BarkCloud.Drive.Engine;
using BarkCloud.Drive.Engine.Tests.Fakes;

using FluentAssertions;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class EngineSettingsStoreTests
{
    [Fact]
    public void Default_cache_directory_is_inside_a_custom_settings_directory()
    {
        using var temp = new TestDirectory();
        var store = new EngineSettingsStore(temp.Root);

        store.GetCacheDir().Should().Be(temp.Path("cache"));
    }
}
