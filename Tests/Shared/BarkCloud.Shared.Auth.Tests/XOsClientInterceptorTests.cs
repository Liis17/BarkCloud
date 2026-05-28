using BarkCloud.Shared.Auth.Tests._Helpers;

using System.Text;

namespace BarkCloud.Shared.Auth.Tests;

public class XOsClientInterceptorTests
{
    [Fact]
    public void AsyncUnary_AddsOsNameInBase64()
    {
        var sut = new XOsClientInterceptor("Android");

        var headers = InterceptorTestHarness.CaptureUnaryHeaders(sut);

        var osName = Encoding.UTF8.GetString(Convert.FromBase64String(headers.GetValue(MetadataKeys.OsName)!));
        osName.Should().Be("Android");
    }

    [Fact]
    public void AsyncUnary_HandlesUnicodeOsName()
    {
        var sut = new XOsClientInterceptor("iOS — 18");

        var headers = InterceptorTestHarness.CaptureUnaryHeaders(sut);

        var osName = Encoding.UTF8.GetString(Convert.FromBase64String(headers.GetValue(MetadataKeys.OsName)!));
        osName.Should().Be("iOS — 18");
    }
}
