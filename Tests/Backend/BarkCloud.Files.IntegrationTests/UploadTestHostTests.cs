using Microsoft.Extensions.Logging;

using Quartz.Logging;

namespace BarkCloud.Files.IntegrationTests;

public sealed class UploadTestHostTests
{
    [Fact]
    public async Task ResolvingBus_AfterPreviousHostDisposed_DoesNotUseDisposedLoggerFactory()
    {
        var postgres = Environment.GetEnvironmentVariable("BARKCLOUD_TEST_POSTGRES");
        var rabbit = Environment.GetEnvironmentVariable("BARKCLOUD_TEST_RABBITMQ");
        Environment.SetEnvironmentVariable("BARKCLOUD_TEST_POSTGRES",
            "Host=127.0.0.1;Database=postgres;Username=postgres;Password=postgres");
        Environment.SetEnvironmentVariable("BARKCLOUD_TEST_RABBITMQ",
            "rabbitmq://integration:integration@127.0.0.1:5672/");
        try
        {
            // Resolve the actual host's bus/Quartz services without starting network connections.
            var database = new UploadTestDatabase();
            var probe = new ProcessingProbe();
            await using (var first = new UploadTestHost(database, probe))
                first.Bus.Should().NotBeNull();

            // Scheduler startup also binds Quartz's global provider to a host-owned factory.
            // Reproduce its disposal without starting PostgreSQL/RabbitMQ connections.
            using (var loggerFactory = new LoggerFactory())
                LogContext.SetCurrentLogProvider(loggerFactory);

            await using var second = new UploadTestHost(database, probe);
            second.Bus.Should().NotBeNull();
        }
        finally
        {
            LogProvider.SetCurrentLogProvider(null);
            MassTransit.LogContext.ConfigureCurrentLogContext();
            Environment.SetEnvironmentVariable("BARKCLOUD_TEST_POSTGRES", postgres);
            Environment.SetEnvironmentVariable("BARKCLOUD_TEST_RABBITMQ", rabbit);
        }
    }
}
