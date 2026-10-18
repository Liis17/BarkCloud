using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace BarkCloud.GrpcServer;

internal sealed record LoggingStartupOptions(bool ConsoleOutput, bool SeqEnable, bool LognaraEnable)
{
    public static LoggingStartupOptions Capture(WebApplicationBuilder builder)
        => new(
            ReadBoolean(builder.Configuration, "ConsoleOutput", defaultValue: true),
            ReadBoolean(builder.Configuration, "SeqEnable", defaultValue: false),
            ReadBoolean(builder.Configuration, "LognaraEnable", defaultValue: false));

    public static IEnumerable<KeyValuePair<string, string?>> ToConfiguration(LoggingStartupOptions options)
    {
        yield return new("ConsoleOutput", options.ConsoleOutput.ToString().ToLowerInvariant());
        yield return new("SeqEnable", options.SeqEnable.ToString().ToLowerInvariant());
        yield return new("LognaraEnable", options.LognaraEnable.ToString().ToLowerInvariant());
    }

    private static bool ReadBoolean(IConfiguration configuration, string key, bool defaultValue)
    {
        var value = configuration[key];
        if (value is null)
            return defaultValue;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        throw new InvalidOperationException(
            $"Invalid startup logging option '{key}={value}'. Expected 'true' or 'false'.");
    }
}
