using System.Net;

using BarkCloud.GrpcServer.Tracker;

namespace BarkCloud.GrpcServer.Tests.Tracker;

public class SourceIpResolverTests
{
    private static readonly IPAddress Proxy = IPAddress.Parse("172.18.0.5");

    [Fact]
    public void Resolve_RealIpHeader_WinsOverConnectionAddress()
        => SourceIpResolver.Resolve("203.0.113.7", Proxy).Should().Be("203.0.113.7");

    [Fact]
    public void Resolve_NoHeader_UsesConnectionAddress()
        => SourceIpResolver.Resolve(null, Proxy).Should().Be("172.18.0.5");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.7, 10.0.0.1")]
    public void Resolve_InvalidHeader_FallsBackToConnectionAddress(string header)
        => SourceIpResolver.Resolve(header, Proxy).Should().Be("172.18.0.5");

    [Fact]
    public void Resolve_NothingKnown_ReturnsNull()
        => SourceIpResolver.Resolve(null, null).Should().BeNull();

    [Fact]
    public void Resolve_Ipv4MappedIpv6_ReturnsIpv4()
    {
        SourceIpResolver.Resolve("::ffff:203.0.113.7", Proxy).Should().Be("203.0.113.7");
        SourceIpResolver.Resolve(null, IPAddress.Parse("::ffff:172.18.0.5")).Should().Be("172.18.0.5");
    }

    [Fact]
    public void Resolve_Ipv6_CollapsesToSlash64()
    {
        var first = SourceIpResolver.Resolve("2001:db8:1:2:aaaa:bbbb:cccc:dddd", Proxy);
        var second = SourceIpResolver.Resolve("2001:db8:1:2:1111:2222:3333:4444", Proxy);

        first.Should().Be("2001:db8:1:2::");
        second.Should().Be(first, "адреса одной /64-подсети — один источник");
        SourceIpResolver.Resolve("2001:db8:1:3::1", Proxy).Should().NotBe(first);
    }
}
