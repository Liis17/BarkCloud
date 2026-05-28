using BarkCloud.Shared.Auth.Tests._Helpers;

using System.Text;

namespace BarkCloud.Shared.Auth.Tests;

public class XDeviceIdInterceptorTests
{
    [Fact]
    public void AsyncUnary_AddsDeviceIdInBase64()
    {
        var sut = new XDeviceIdInterceptor("device-id-42");

        var headers = InterceptorTestHarness.CaptureUnaryHeaders(sut);

        var deviceId = Encoding.UTF8.GetString(Convert.FromBase64String(headers.GetValue(MetadataKeys.DeviceId)!));
        deviceId.Should().Be("device-id-42");
    }
}
