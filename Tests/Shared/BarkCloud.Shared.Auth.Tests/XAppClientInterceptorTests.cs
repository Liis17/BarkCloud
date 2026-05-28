using BarkCloud.Shared.Auth.Tests._Helpers;

using System.Text;

namespace BarkCloud.Shared.Auth.Tests;

public class XAppClientInterceptorTests
{
    [Fact]
    public void AsyncUnary_AddsAppNameAndVersionInBase64()
    {
        var sut = new XAppClientInterceptor("BarkCloud", "1.2.3");

        var headers = InterceptorTestHarness.CaptureUnaryHeaders(sut);

        var appName = Encoding.UTF8.GetString(Convert.FromBase64String(headers.GetValue(MetadataKeys.AppName)!));
        var appVersion = Encoding.UTF8.GetString(Convert.FromBase64String(headers.GetValue(MetadataKeys.AppVersion)!));

        appName.Should().Be("BarkCloud");
        appVersion.Should().Be("1.2.3");
    }
}
