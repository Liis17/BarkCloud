using System.Collections.Concurrent;

using BarkCloud.Files.Consumers;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Scheduling;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Queue.Files;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Quartz;

namespace BarkCloud.Files.IntegrationTests;

internal sealed class ProcessingProbe : IConsumeObserver
{
    public ConcurrentDictionary<Guid, Func<UploadSession, FilesContext, CancellationToken, Task>> Pipelines { get; } = new();
    public ConcurrentDictionary<Guid, ConcurrentQueue<int>> Deliveries { get; } = new();
    public ConcurrentDictionary<Guid, int> PipelineCalls { get; } = new();
    public ConcurrentDictionary<Guid, int> Cleanups { get; } = new();
    public ConcurrentQueue<Fault<ProcessUploadedFile>> Faults { get; } = new();
    public int Active;
    public int MaximumActive;

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        if (context.Message is ProcessUploadedFile message)
            Deliveries.GetOrAdd(message.SessionId, _ => new()).Enqueue(context.GetRedeliveryCount());
        return Task.CompletedTask;
    }
    public ConcurrentDictionary<Guid, int> Completed { get; } = new();
    public Task PostConsume<T>(ConsumeContext<T> context) where T : class
    {
        if (context.Message is ProcessUploadedFile message)
            Completed.AddOrUpdate(message.SessionId, 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }
    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class => Task.CompletedTask;
}

internal sealed class ProbePipeline(FilesContext database, ProcessingProbe probe) : IUploadEnrichmentPipeline
{
    public async Task ProcessAsync(UploadSession session, CancellationToken cancellationToken)
    {
        probe.PipelineCalls.AddOrUpdate(session.Id, 1, (_, count) => count + 1);
        var active = Interlocked.Increment(ref probe.Active);
        int previous;
        do { previous = Volatile.Read(ref probe.MaximumActive); }
        while (active > previous && Interlocked.CompareExchange(ref probe.MaximumActive, active, previous) != previous);
        try
        {
            if (probe.Pipelines.TryGetValue(session.Id, out var pipeline))
                await pipeline(session, database, cancellationToken);
            else
            {
                var file = await database.UploadedFiles.SingleAsync(x => x.Id == session.FileId, cancellationToken);
                file.Etag = session.CompletedEtag;
                file.Size = session.DeclaredSize;
            }
        }
        finally { Interlocked.Decrement(ref probe.Active); }
    }
}

internal sealed class UploadTestHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly ProcessingProbe _probe;
    private IDisposable? _observer;

    public UploadTestHost(UploadTestDatabase database, ProcessingProbe probe, IReadOnlyList<TimeSpan>? intervals = null)
    {
        _probe = probe;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(probe);
        builder.Services.AddSingleton<MetricsCollector>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<FilesContext>(o => o.UseNpgsql(database.ConnectionString));
        builder.Services.AddScoped<UploadSessionProcessor>();
        builder.Services.AddScoped<IUploadEnrichmentPipeline, ProbePipeline>();
        builder.Services.AddScoped<IUploadArtifactCleaner, UploadArtifactCleaner>();
        builder.Services.AddScoped<ITrashPurgeService>(services =>
        {
            var context = services.GetRequiredService<FilesContext>();
            var purge = new Mock<ITrashPurgeService>();
            purge.Setup(x => x.PurgeOrphanBlobsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .Returns<IReadOnlyCollection<Guid>, CancellationToken>(async (ids, token) =>
                {
                    foreach (var session in await context.UploadSessions.Where(x => ids.Contains(x.FileId)).ToListAsync(token))
                        probe.Cleanups.AddOrUpdate(session.Id, 1, (_, count) => count + 1);
                    return await context.UploadedFiles.Where(x => ids.Contains(x.Id) && x.Uploaders.Count == 0).ExecuteDeleteAsync(token);
                });
            return purge.Object;
        });
        builder.Services.AddUploadScheduler(database.ConnectionString);
        builder.Services.Configure<MassTransitHostOptions>(o =>
        {
            o.WaitUntilStarted = true;
            o.StartTimeout = TimeSpan.FromSeconds(60);
            o.StopTimeout = TimeSpan.FromSeconds(15);
            o.ConsumerStopTimeout = TimeSpan.FromSeconds(1);
        });
        builder.Services.AddMassTransit(x =>
        {
            x.AddMessageScheduler(UploadProcessingQueue.SchedulerAddress);
            x.AddQuartzConsumers(o => o.QueueName = UploadProcessingQueue.SchedulerQueueName);
            x.AddConsumer<ProcessUploadedFileConsumer>();
            x.UsingRabbitMq((context, cfg) =>
            {
                var address = RabbitAddress;
                cfg.Host(address, h =>
                {
                    var credentials = address.UserInfo.Split(':', 2);
                    h.Username(Uri.UnescapeDataString(credentials[0]));
                    h.Password(Uri.UnescapeDataString(credentials[1]));
                });
                cfg.ConfigureUploadScheduler(context);
                cfg.ReceiveEndpoint("process-uploaded-file", e => e.ConfigureUploadProcessing(context, intervals));
                cfg.ReceiveEndpoint($"f18-faults-{Guid.NewGuid():N}", e =>
                {
                    e.Durable = false;
                    e.AutoDelete = true;
                    e.Handler<Fault<ProcessUploadedFile>>(c => { probe.Faults.Enqueue(c.Message); return Task.CompletedTask; });
                });
            });
        });
        _host = builder.Build();
    }

    public static Uri RabbitAddress => new(Environment.GetEnvironmentVariable("BARKCLOUD_TEST_RABBITMQ")
        ?? throw new InvalidOperationException("Set BARKCLOUD_TEST_RABBITMQ to an isolated test broker."));
    public IBus Bus => _host.Services.GetRequiredService<IBus>();
    public Task<IScheduler> SchedulerAsync() => _host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
    public async Task StartAsync()
    {
        _observer = Bus.ConnectConsumeObserver(_probe);
        await _host.StartAsync();
    }
    public async Task SendAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var endpoint = await Bus.GetSendEndpoint(new Uri("queue:process-uploaded-file"));
        await endpoint.Send(new ProcessUploadedFile(id), timeout.Token);
    }
    public async ValueTask DisposeAsync()
    {
        try { await _host.StopAsync(TimeSpan.FromSeconds(20)); }
        finally { _observer?.Dispose(); _host.Dispose(); }
    }
}
