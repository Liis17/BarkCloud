using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.TestKit;

public static class TestLoggers
{
    public static ILogger<T> Null<T>() => NullLogger<T>.Instance;
}
