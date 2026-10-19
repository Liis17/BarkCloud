using System.Diagnostics;

using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer;
using BarkCloud.Shared.Queue.Files;

using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BarkCloud.Files.Tests.Tracing;

[Collection("Trace propagation")]
public sealed class OutboxTracePersistenceTests
{
    [Fact]
    public async Task BusOutbox_PersistsTraceHeadersBeyondProducerScope()
    {
        var loggingBuilder = WebApplication.CreateBuilder();
        loggingBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConsoleOutput"] = "false", ["SeqEnable"] = "false", ["LognaraEnable"] = "false"
        });
        await using var logging = await loggingBuilder.AddBarkCloudSerilogAsync("Files.OutboxTraceTest");
        using var database = new SqliteFilesContext();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<FilesContext>(options =>
            options.UseSqlite(database.Context.Database.GetDbConnection()));
        builder.Services.AddMassTransit(registration =>
        {
            registration.AddEntityFrameworkOutbox<FilesContext>(options =>
            {
                // Delivery is disabled: exercise the real serializer/EF outbox without PostgreSQL locks or a broker.
                options.UsePostgres();
                options.UseBusOutbox(outbox => outbox.DisableDeliveryService());
            });
            registration.UsingInMemory();
        });
        using var host = builder.Build();
        MassTransit.LogContext.ConfigureCurrentLogContext(host.Services.GetRequiredService<ILoggerFactory>());
        await host.StartAsync();
        try
        {
            ActivityContext original;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                using var operation = BarkCloudDiagnostics.StartOperation("Files.UploadComplete");
                original = operation.Context;
                await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>()
                    .Publish(new ProcessUploadedFile(Guid.NewGuid()));
                await scope.ServiceProvider.GetRequiredService<FilesContext>().SaveChangesAsync();
            }

            await using var reader = database.CreateAdditionalContext();
            var saved = await reader.Set<OutboxMessage>().SingleAsync();
            saved.Headers.Should().Contain(original.TraceId.ToHexString());
            Activity.Current.Should().BeNull();
        }
        finally
        {
            await host.StopAsync();
            MassTransit.LogContext.ConfigureCurrentLogContext();
        }
    }
}
