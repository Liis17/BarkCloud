using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace BarkCloud.Web.Infrastructure;

internal static class TrustedProxyNetworks
{
    private const string ConfigurationKey = "App:TrustedProxyNetworks";

    private static readonly IPNetwork[] DefaultNetworks =
    [
        new(IPAddress.Parse("127.0.0.0"), 8),
        new(IPAddress.IPv6Loopback, 128),
        new(IPAddress.Parse("10.0.0.0"), 8),
        new(IPAddress.Parse("172.16.0.0"), 12),
        new(IPAddress.Parse("192.168.0.0"), 16)
    ];

    public static IReadOnlyList<IPNetwork> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultNetworks;

        var networks = new List<IPNetwork>();
        foreach (var item in value.Split([';', ','], StringSplitOptions.TrimEntries))
        {
            if (!TryParse(item, out var network))
                throw new InvalidOperationException($"{ConfigurationKey} contains invalid network '{item}'.");

            networks.Add(network);
        }

        return networks;
    }

    private static bool TryParse(string value, out IPNetwork network)
    {
        network = default;
        var slashIndex = value.IndexOf('/');
        var addressValue = slashIndex < 0 ? value : value[..slashIndex];
        if (!IPAddress.TryParse(addressValue, out var address))
            return false;

        var prefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (slashIndex >= 0
            && !int.TryParse(value[(slashIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength))
            return false;

        if (address.IsIPv4MappedToIPv6)
        {
            if (prefixLength < 96 || prefixLength > 128)
                return false;

            address = address.MapToIPv4();
            prefixLength -= 96;
        }

        var maxPrefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
            return false;

        network = new IPNetwork(address, prefixLength);
        return true;
    }
}
