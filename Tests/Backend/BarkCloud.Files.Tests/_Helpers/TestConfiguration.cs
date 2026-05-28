using Microsoft.Extensions.Configuration;

namespace BarkCloud.Files.Tests._Helpers;

internal static class TestConfiguration
{
    public static IConfiguration Empty()
        => new ConfigurationBuilder().Build();

    public static IConfiguration With(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();
}
