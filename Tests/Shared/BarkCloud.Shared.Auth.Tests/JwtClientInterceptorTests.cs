using BarkCloud.Shared.Auth.Tests._Helpers;

namespace BarkCloud.Shared.Auth.Tests;

public class JwtClientInterceptorTests
{
    [Fact]
    public void AsyncUnary_AddsTokenHeader()
    {
        var sut = new JwtClientInterceptor("jwt-value");

        var headers = InterceptorTestHarness.CaptureUnaryHeaders(sut);

        headers.GetValue(MetadataKeys.Token).Should().Be("jwt-value");
    }

    [Fact]
    public void AsyncUnary_PreservesExistingHeaders()
    {
        var existing = new Metadata { { "x-other", "v" } };
        var sut = new JwtClientInterceptor("jwt-value");

        var headers = InterceptorTestHarness.CaptureUnaryHeaders(sut, existing);

        headers.GetValue("x-other").Should().Be("v");
        headers.GetValue(MetadataKeys.Token).Should().Be("jwt-value");
    }
}
