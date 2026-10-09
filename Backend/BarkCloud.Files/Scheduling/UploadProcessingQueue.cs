using BarkCloud.Files.Consumers;

using MassTransit;

using Quartz;

namespace BarkCloud.Files.Scheduling;

public static class UploadProcessingQueue
{
    public const string SchedulerQueueName = "files-upload-scheduler";
    public static readonly Uri SchedulerAddress = new($"queue:{SchedulerQueueName}");

    public static IReadOnlyList<TimeSpan> RedeliveryIntervals { get; } = Array.AsReadOnly(new[]
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    });

    public static void AddUploadScheduler(this IServiceCollection services, string connectionString)
    {
        services.AddQuartz(q =>
        {
            q.SchedulerName = "BarkCloud.Files.UploadScheduler";
            q.SchedulerId = "AUTO";
            q.UsePersistentStore(store =>
            {
                store.UseProperties = true;
                store.UsePostgres(postgres =>
                {
                    postgres.ConnectionString = connectionString;
                    postgres.TablePrefix = "files_quartz.qrtz_";
                });
                store.UseClustering();
                store.UseSystemTextJsonSerializer();
            });
            q.AddJobListener<ScheduledMessageRecoveryListener>();
        });
        // AddQuartzConsumers owns startup/shutdown after bus readiness; no Quartz hosted service.
    }

    public static void ConfigureUploadScheduler(this IRabbitMqBusFactoryConfigurator bus,
        IBusRegistrationContext context)
    {
        bus.UseMessageScheduler(SchedulerAddress);
        bus.ReceiveEndpoint(SchedulerQueueName, endpoint =>
        {
            endpoint.Durable = true;
            endpoint.AutoDelete = false;
            endpoint.ConfigureQuartzConsumers(context);
        });
    }

    public static void ConfigureUploadProcessing(this IReceiveEndpointConfigurator endpoint,
        IBusRegistrationContext context, IReadOnlyList<TimeSpan>? intervals = null)
    {
        // Bus outbox covers Complete + publish. S3/ffmpeg runs outside an EF transaction;
        // durable session state makes processing idempotent on redelivery.
        endpoint.ConcurrentMessageLimit = 2;
        endpoint.UseScheduledRedelivery(r => r.Intervals((intervals ?? RedeliveryIntervals).ToArray()));
        endpoint.ConfigureConsumer<ProcessUploadedFileConsumer>(context);
    }
}
