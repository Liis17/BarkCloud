using System.Text;

using BarkCloud.Shared.Auth;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

using Microsoft.AspNetCore.Http;

namespace BarkCloud.Web.Tests.Infrastructure;

public class BrowserContextTests
{
    [Fact]
    public void UserTokenWithDevice_IncludesAuthTokenAndDeviceMetadata()
    {
        var device = new DeviceInfo(
            DeviceName: "Google Chrome",
            Os: "macOS",
            AppName: "BarkCloud Web",
            AppVersion: "v1.0.0",
            DeviceId: "device-1",
            Ip: "127.0.0.1");

        var metadata = BrowserContext.UserTokenWithDevice("access-token", device);

        Value(metadata, MetadataKeys.Token).Should().Be("access-token");
        Decode(Value(metadata, MetadataKeys.DeviceName)).Should().Be("Google Chrome");
        Decode(Value(metadata, MetadataKeys.OsName)).Should().Be("macOS");
        Decode(Value(metadata, MetadataKeys.AppName)).Should().Be("BarkCloud Web");
        Decode(Value(metadata, MetadataKeys.DeviceId)).Should().Be("device-1");
    }

    [Fact]
    public void ToMetadata_WithIp_PassesTrustedRealIpAsPlainValue()
    {
        var device = new DeviceInfo("Chrome", "macOS", "BarkCloud Web", "v1", "device-1", Ip: "203.0.113.7");

        var metadata = device.ToMetadata();

        Value(metadata, MetadataKeys.RealIp).Should().Be("203.0.113.7");
        Decode(Value(metadata, MetadataKeys.IpAddress)).Should().Be("203.0.113.7");
    }

    [Fact]
    public void ToMetadata_WithoutIp_HasNoIpMetadata()
    {
        var device = new DeviceInfo("Chrome", "macOS", "BarkCloud Web", "v1", "device-1", Ip: "");

        var metadata = device.ToMetadata();

        metadata.Select(e => e.Key).Should().NotContain(new[] { MetadataKeys.RealIp, MetadataKeys.IpAddress });
    }

    [Fact]
    public void ResolveIp_UsesRealIpAndIgnoresClientForwardedFor()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 203.0.113.7";
        http.Request.Headers["X-Real-IP"] = "203.0.113.7";
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.18.0.5");

        BrowserContext.ResolveIp(http).Should().Be("203.0.113.7");
    }

    [Fact]
    public void ResolveIp_ForgedForwardedForWithoutRealIp_FallsBackToConnection()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Forwarded-For"] = "1.2.3.4";
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("::ffff:172.18.0.5");

        BrowserContext.ResolveIp(http).Should().Be("172.18.0.5");
    }

    [Fact]
    public void ResolveIp_NothingKnown_ReturnsEmpty()
        => BrowserContext.ResolveIp(new DefaultHttpContext()).Should().BeEmpty();

    private static string Value(Metadata metadata, string key)
        => metadata.First(e => e.Key == key).Value;

    private static string Decode(string value)
        => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
