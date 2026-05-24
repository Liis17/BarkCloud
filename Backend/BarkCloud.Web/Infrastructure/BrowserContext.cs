using BarkCloud.Shared.Auth;

using Grpc.Core;

namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Помощники для перевода HTTP-запроса браузера в gRPC-вызов к микросервисам.
/// </summary>
public static class BrowserContext
{
    /// <summary>Метаданные с пользовательским access-токеном (передаётся как есть).</summary>
    public static Metadata UserToken(string accessToken)
        => new() { { MetadataKeys.Token, accessToken } };

    public static DeviceInfo BuildDeviceInfo(HttpContext http, string deviceId, string appName, string appVersion)
    {
        var ua = http.Request.Headers.UserAgent.ToString();

        return new DeviceInfo(
            DeviceName: DescribeBrowser(ua),
            Os: DescribeOs(ua),
            AppName: appName,
            AppVersion: appVersion,
            DeviceId: deviceId,
            Ip: ResolveIp(http));
    }

    private static string ResolveIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();

        var ip = http.Connection.RemoteIpAddress;
        if (ip is null)
            return string.Empty;

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        return ip.ToString();
    }

    private static string DescribeBrowser(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "Браузер";
        if (ua.Contains("Edg", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (ua.Contains("OPR", StringComparison.OrdinalIgnoreCase) || ua.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "Opera";
        if (ua.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Mozilla Firefox";
        if (ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        if (ua.Contains("Safari", StringComparison.OrdinalIgnoreCase)) return "Safari";
        return "Браузер";
    }

    private static string DescribeOs(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "Web";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (ua.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "macOS";
        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
        return "Web";
    }
}
