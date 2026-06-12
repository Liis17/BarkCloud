using System.Text;

using BarkCloud.Shared.Auth;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

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

    private static string Value(Metadata metadata, string key)
        => metadata.First(e => e.Key == key).Value;

    private static string Decode(string value)
        => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
