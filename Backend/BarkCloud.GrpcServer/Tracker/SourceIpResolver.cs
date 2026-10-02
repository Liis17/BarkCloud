using System.Net;
using System.Net.Sockets;

namespace BarkCloud.GrpcServer.Tracker;

/// <summary>
/// Доверенный адрес источника запроса для лимитов попыток. В отличие от <see cref="RequestContext.IpAddress"/>
/// не берёт ничего, что клиент может подставить сам: ни метаданные <c>x-ip-address</c>, ни <c>X-Forwarded-For</c>
/// (nginx лишь дописывает свой адрес к присланному клиентом значению). Остаются <c>X-Real-IP</c>, который nginx
/// перезаписывает значением <c>$remote_addr</c> (Web передаёт так же адрес браузера на внутреннем вызове),
/// и адрес TCP-соединения. Предполагается, что сервис недоступен снаружи в обход nginx.
/// </summary>
public static class SourceIpResolver
{
    public static string? Resolve(string? realIpHeader, IPAddress? remoteIp)
    {
        if (IPAddress.TryParse(realIpHeader?.Trim(), out var forwarded))
        {
            return Normalize(forwarded);
        }

        return remoteIp is null ? null : Normalize(remoteIp);
    }

    /// <summary>IPv4-mapped → IPv4; IPv6 сводится к префиксу /64, иначе один хост перебирает адреса своей подсети.</summary>
    private static string Normalize(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4().ToString();
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString();
    }
}
