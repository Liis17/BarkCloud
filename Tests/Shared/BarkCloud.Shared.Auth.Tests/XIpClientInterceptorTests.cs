using BarkCloud.Shared.Auth.Tests._Helpers;

using System.Text;

namespace BarkCloud.Shared.Auth.Tests;

public class XIpClientInterceptorTests
{
    [Fact]
    public void AsyncUnary_AddsIpAddressInBase64()
    {
        var sut = new XIpClientInterceptor("192.168.1.1");

        var headers = InterceptorTestHarness.CaptureUnaryHeaders(sut);

        var ip = Encoding.UTF8.GetString(Convert.FromBase64String(headers.GetValue(MetadataKeys.IpAddress)!));
        ip.Should().Be("192.168.1.1");
    }
}
