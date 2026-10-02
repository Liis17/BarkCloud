using System.Threading.Channels;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Queue.Users;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Contexts;
using BarkCloud.Users.Persistence.Services;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BarkCloud.Users.IntegrationTests;

internal sealed class OutboxTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    public OutboxTestHost(PostgresUsersDatabase database, bool deliver = true, Uri? address = null,
        params IInterceptor[] interceptors)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        builder.Services.AddSingleton<MetricsCollector>();
        builder.Services.AddDbContext<UsersContext>(o => o.UseNpgsql(database.ConnectionString).AddInterceptors(interceptors));
        builder.Services.AddTransient<IUsersStorage, UsersStorage>();
        builder.Services.AddScoped<UserInfoQueueSender>();
        builder.Services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<UsersContext>(o =>
            {
                o.UsePostgres();
                o.QueryDelay = TimeSpan.FromMilliseconds(100);
                o.UseBusOutbox(b =>
                {
                    b.MessageDeliveryTimeout = TimeSpan.FromSeconds(2);
                    if (!deliver)
                        b.DisableDeliveryService();
                });
            });
            x.UsingRabbitMq((_, cfg) => ConfigureRabbit(cfg, address ?? RabbitAddress));
        });
        _host = builder.Build();
    }

    public static Uri RabbitAddress => new(Environment.GetEnvironmentVariable("BARKCLOUD_TEST_RABBITMQ")
        ?? throw new InvalidOperationException("Задайте BARKCLOUD_TEST_RABBITMQ для интеграционных тестов outbox."));

    public IServiceProvider Services => _host.Services;

    internal static void ConfigureRabbit(IRabbitMqBusFactoryConfigurator cfg, Uri address)
    {
        cfg.Host(address, h =>
        {
            if (string.IsNullOrEmpty(address.UserInfo))
                return;
            var credentials = address.UserInfo.Split(':', 2);
            h.Username(Uri.UnescapeDataString(credentials[0]));
            h.Password(credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty);
        });
    }

    public Task StartAsync() => _host.StartAsync();

    public async ValueTask DisposeAsync()
    {
        try { await _host.StopAsync(TimeSpan.FromSeconds(10)); }
        finally { _host.Dispose(); }
    }
}

internal sealed class RabbitObserver : IAsyncDisposable
{
    private readonly Channel<object> _messages = Channel.CreateUnbounded<object>();
    private readonly IBusControl _bus;

    public RabbitObserver()
    {
        _bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            OutboxTestHost.ConfigureRabbit(cfg, OutboxTestHost.RabbitAddress);
            cfg.ReceiveEndpoint($"barkcloud-f11-{Guid.NewGuid():N}", e =>
            {
                e.Durable = false;
                e.AutoDelete = true;
                e.Handler<UserDeleted>(c => Record(c.Message));
                e.Handler<UserChangedName>(c => Record(c.Message));
                e.Handler<UserChangedUsername>(c => Record(c.Message));
                e.Handler<UserChangedAvatar>(c => Record(c.Message));
                e.Handler<UserChangedBio>(c => Record(c.Message));
            });
        });
    }

    private Task Record(object message)
    {
        _messages.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    public async Task StartAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _bus.StartAsync(timeout.Token);
    }

    public async Task<object[]> ReceiveAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var messages = new List<object>();
        while (messages.Count < count)
            messages.Add(await _messages.Reader.ReadAsync(timeout.Token));
        return messages.ToArray();
    }

    public ValueTask DisposeAsync() => new(_bus.StopAsync(TimeSpan.FromSeconds(10)));
}
