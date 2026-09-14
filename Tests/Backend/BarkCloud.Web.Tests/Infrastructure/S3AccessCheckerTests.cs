using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class S3AccessCheckerTests
{
    [Fact]
    public async Task CheckAsync_RejectsInvalidEndpointWithoutConnecting()
    {
        var result = await new S3AccessChecker().CheckAsync(new S3AccessCheckRequest(
            "not-an-url",
            "access",
            "secret",
            "bucket",
            false));

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Endpoint S3 должен быть абсолютным HTTP(S)-адресом");
    }

    [Fact]
    public async Task CheckAsync_RequiresCredentialsAndBucket()
    {
        var result = await new S3AccessChecker().CheckAsync(new S3AccessCheckRequest(
            "http://minio:9000",
            "",
            "",
            "",
            false));

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Укажите access key");
    }
}
