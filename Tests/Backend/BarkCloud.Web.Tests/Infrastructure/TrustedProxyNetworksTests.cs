using System.Net;

using BarkCloud.Web.Infrastructure;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class TrustedProxyNetworksTests
{
    [Fact]
    public void MissingOrBlankValueUsesLoopbackAndPrivateNetworkDefaults()
    {
        var expected = new[]
        {
            "127.0.0.0/8",
            "::1/128",
            "10.0.0.0/8",
            "172.16.0.0/12",
            "192.168.0.0/16"
        };

        Assert.Equal(expected, TrustedProxyNetworks.Parse(null).Select(network => $"{network.BaseAddress}/{network.PrefixLength}"));
        Assert.Equal(expected, TrustedProxyNetworks.Parse(" 	 ").Select(network => $"{network.BaseAddress}/{network.PrefixLength}"));
    }

    [Fact]
    public void ParsesCustomNetwork()
    {
        var network = Assert.Single(TrustedProxyNetworks.Parse("10.1.0.0/16"));

        Assert.Equal("10.1.0.0/16", $"{network.BaseAddress}/{network.PrefixLength}");
    }

    [Fact]
    public void AcceptsSemicolonAndCommaSeparatorsInTheSameValue()
    {
        var networks = TrustedProxyNetworks.Parse("10.1.0.0/16; 192.168.1.0/24,172.16.0.0/12");

        Assert.Equal(
            new[] { "10.1.0.0/16", "192.168.1.0/24", "172.16.0.0/12" },
            networks.Select(network => $"{network.BaseAddress}/{network.PrefixLength}"));
    }

    [Fact]
    public void SingleAddressesUseFullPrefixLengths()
    {
        var networks = TrustedProxyNetworks.Parse("192.0.2.10,2001:db8::1");

        Assert.Equal(
            new[] { "192.0.2.10/32", "2001:db8::1/128" },
            networks.Select(network => $"{network.BaseAddress}/{network.PrefixLength}"));
    }

    [Fact]
    public void IPv4MappedIPv6AddressIsNormalizedToIPv4()
    {
        var network = Assert.Single(TrustedProxyNetworks.Parse("::ffff:172.16.0.0"));

        Assert.Equal(IPAddress.Parse("172.16.0.0"), network.BaseAddress);
        Assert.Equal(32, network.PrefixLength);
    }

    [Theory]
    [InlineData("10.0.0.0/abc")]
    [InlineData("not-a-network")]
    public void InvalidNetworkThrowsWithSettingNameAndValue(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TrustedProxyNetworks.Parse(value));

        Assert.Contains("App:TrustedProxyNetworks", exception.Message);
        Assert.Contains(value, exception.Message);
    }

    [Theory]
    [InlineData("172.17.0.5", "forwarded.example", "https")]
    [InlineData("203.0.113.10", "direct.example", "http")]
    [InlineData("::ffff:172.17.0.5", "forwarded.example", "https")]
    public async Task ForwardedHeadersMiddlewareOnlyUsesHeadersFromTrustedNetworks(
        string remoteAddress,
        string expectedHost,
        string expectedScheme)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
        };
        options.KnownIPNetworks.Clear();
        foreach (var network in TrustedProxyNetworks.Parse("172.17.0.0/16"))
            options.KnownIPNetworks.Add(network);

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Host = new HostString("direct.example");
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Host"] = "forwarded.example";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await middleware.Invoke(context);

        Assert.Equal(expectedHost, context.Request.Host.Host);
        Assert.Equal(expectedScheme, context.Request.Scheme);
    }
}
