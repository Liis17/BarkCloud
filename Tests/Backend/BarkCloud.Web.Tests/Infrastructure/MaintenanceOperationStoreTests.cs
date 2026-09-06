using BarkCloud.Web.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Infrastructure;

public sealed class MaintenanceOperationStoreTests
{
    [Fact]
    public async Task ReadAsync_LoadsPersistentFailureAndHelperLog()
    {
        var directory = Directory.CreateTempSubdirectory("barkcloud-maintenance");
        try
        {
            var statePath = Path.Combine(directory.FullName, "last-operation.json");
            var logPath = Path.Combine(directory.FullName, "self-update.log");
            await File.WriteAllTextAsync(statePath, """
                {"operationId":"abc","kind":"update","state":"failed","message":"rollback","diagnostic":"Лог helper: self-update.log","updatedAtUtc":"2026-09-06T10:00:00Z"}
                """);
            await File.WriteAllTextAsync(logPath, "docker run cloud-web\nКод: 137\nstderr: healthcheck failed");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Docker:MaintenanceStateFile"] = statePath,
                    ["Docker:MaintenanceLogFile"] = logPath,
                })
                .Build();
            var store = new MaintenanceOperationStore(configuration, NullLogger<MaintenanceOperationStore>.Instance);

            var result = await store.ReadAsync();

            result.Should().NotBeNull();
            result!.State.Should().Be("failed");
            result.Diagnostic.Should().Contain("Код: 137");
            result.Diagnostic.Should().Contain("healthcheck failed");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
