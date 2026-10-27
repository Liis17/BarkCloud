using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.ServiceAuth;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.SessionRevocation;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Interceptors;
using BarkCloud.Shared.Identity;
using BarkCloud.Users.Host;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Contexts;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Services;

using MassTransit;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Users;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        await builder.LoadConfigurationAsync(ServiceId.Users);
        await using var loggingLifetime = await builder.AddBarkCloudSerilogAsync("BarkCloud.Users");
        builder.SetRunningAddress(builder.Configuration);

        // Регистрируем gRPC сервисы с интерцепторами
        builder.Services.AddBarkCloudGrpc();
        builder.Services.AddBarkCloudMetrics("BarkCloud.Users");

        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

        builder.Services.AddGrpcReflection();

        builder.Services.AddDbContext<UsersContext>(c
            => c.UseNpgsql(builder.Configuration["UsersDb"]));

        builder.Services.AddTransient<IUsersStorage, UsersStorage>();
        builder.Services.AddTransient<IDevicesStorage, DevicesStorage>();
        builder.Services.AddScoped<UserInfoQueueSender>();
        builder.Services.AddSingleton<ReservedUsernamesService>();

        // Регистрируем аутентификацию и авторизацию
        builder.Services.AddXAuth(builder.Configuration, ServiceId.Users);
        builder.Services.AddSingleton<IRevocationFeed, GrpcRevocationFeed>();

        // Сервисные токены: выдаёт Identity по секрету сервиса (ServiceAuth:Credential / SERVICE_CREDENTIAL).
        builder.Services.AddServiceTokenClient(builder.Configuration);
        builder.Services.AddGrpcClient<SessionRevocationApi.SessionRevocationApiClient>(o =>
            {
                o.Address = new Uri(builder.Configuration["IdentityService:Host"]!);
            }).AddInterceptor(sp => new ServiceTokenClientInterceptor(
                sp.GetRequiredService<ServiceTokenProvider>(), ServiceId.Identity))
            .AddInterceptor(() => new ExceptionClientInterceptor());

        builder.Services.AddGrpcClient<FilesServerApi.FilesServerApiClient>(o =>
            {
                o.Address = new Uri(builder.Configuration["FilesService:Host"]);
            }).AddInterceptor(sp => new ServiceTokenClientInterceptor(
                sp.GetRequiredService<ServiceTokenProvider>(), ServiceId.Files))
            .AddInterceptor(() => new ExceptionClientInterceptor());

        builder.Services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<UsersContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(builder.Configuration["RabbitMQ:Host"], builder.Configuration["RabbitMQ:VirtualHost"], h =>
                {
                    h.Username(builder.Configuration["RabbitMQ:Username"]);
                    h.Password(builder.Configuration["RabbitMQ:Password"]);
                });
                cfg.OverrideDefaultBusEndpointQueueName("bark-users-bus-" + Guid.NewGuid().ToString("N"));
            });
        });

        var app = builder.Build();

        // Гейджи: время старта и health-флаг миграции (0 — не применена / упала, 1 — успех).
        var startupMetrics = app.Services.GetRequiredService<MetricsCollector>();
        startupMetrics.Set("service_started_unix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        startupMetrics.Set("db_migration_healthy", 0);

        // Применение миграций базы данных
        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<UsersContext>();
            ctx.Database.Migrate();
            startupMetrics.Set("db_migration_healthy", 1);
        }

        app.MapGrpcReflectionService();

        // Настраиваем middleware pipeline
        app.UseRouting();

        app.UseXAuth();

        // Регистрируем gRPC сервисы
        app.MapGrpcService<UsersServerApiService>();
        app.MapGrpcService<UsersApiService>();

        await app.RunAsync();
    }
}
