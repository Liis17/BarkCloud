using BarkCloud.Shared.Auth.Tests._Helpers;

using System.Text;

namespace BarkCloud.Shared.Auth.Tests;

public class XDeviceClientInterceptorTests
{
    [Fact]
    public void AsyncUnary_AddsDeviceNameInBase64()
    {
        var sut = new XDeviceClientInterceptor("Pixel 8 Pro");

        var headers = InterceptorTestHarness.CaptureUnaryHeaders(sut);

        var deviceName = Encoding.UTF8.GetString(Convert.FromBase64String(headers.GetValue(MetadataKeys.DeviceName)!));
        deviceName.Should().Be("Pixel 8 Pro");
    }
}
