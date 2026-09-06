using BarkCloud.Files.Infrastructure;

using System.Net.Sockets;

namespace BarkCloud.Files.Tests.Infrastructure;

public sealed class S3BucketInitializerTests
{
    [Fact]
    public void IsTransientStartupException_RecognisesConnectionRefused()
    {
        var exception = new HttpRequestException(
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused));

        S3BucketInitializer.IsTransientStartupException(exception).Should().BeTrue();
    }

    [Fact]
    public void IsTransientStartupException_DoesNotRetryPermanentErrors()
    {
        S3BucketInitializer.IsTransientStartupException(new UnauthorizedAccessException()).Should().BeFalse();
    }
}
