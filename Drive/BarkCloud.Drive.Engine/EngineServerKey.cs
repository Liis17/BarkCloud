namespace BarkCloud.Drive.Engine;

internal static class EngineServerKey
{
    public static string FromConfig(EngineConfig config)
    {
        var identity = new UriBuilder(Uri.UriSchemeHttps, config.Host.Trim(), config.IdentityPort).Uri;
        var host = identity.IdnHost.ToLowerInvariant();
        if (identity.HostNameType == UriHostNameType.IPv6)
            host = $"[{host}]";
        var identityAddress = $"https://{host}:{identity.Port}";
        return $"{identityAddress}|files:{config.FilesPort}|users:{config.UsersPort}";
    }
}
