using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Auth;
using BarkCloud.Shared.Exceptions.Interceptors;
using BarkCloud.Shared.Identity;
using BarkCloud.Users.Consumers;
using BarkCloud.Users.Host;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Contexts;
using BarkCloud.Users.Persistence.Services;
using BarkCloud.Users.Services;

using MassTransit;

using Microsoft.EntityFrameworkCore;

using Serilog;

namespace BarkCloud.Users;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.LoadConfiguration(ServiceId.Users);
        builder.AddBarkCloudSerilog("BarkCloud.Users");
        builder.SetRunningAddress(builder.Configuration);

        // Регистрируем gRPC сервисы с интерцепторами
        builder.Services.AddBarkCloudGrpc();
        builder.Services.AddBarkCloudMetrics("BarkCloud.Users");

        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

        builder.Services.AddGrpcReflection();

        builder.Services.AddDbContext<UsersContext>(c
            => c.UseNpgsql(builder.Configuration["UsersDb"]));

        builder.Services.AddTransient<UsersStorage>();
        builder.Services.AddTransient<DevicesStorage>();
        builder.Services.AddScoped<UserInfoQueueSender>();
        builder.Services.AddSingleton<ReservedUsernamesService>();

        // Регистрируем аутентификацию и авторизацию
        builder.Services.AddXAuth(builder.Configuration);

        builder.Services.AddGrpcClient<FilesServerApi.FilesServerApiClient>(o =>
            {
                o.Address = new Uri(builder.Configuration["FilesService:Host"]);
            }).AddInterceptor(() => new JwtClientInterceptor(builder.Configuration["FilesService:Token"]))
            .AddInterceptor(() => new ExceptionClientInterceptor());

        builder.Services.AddMassTransit(x =>
        {
            x.AddConsumer<SessionRevokedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(builder.Configuration["RabbitMQ:Host"], "/", h =>
                {
                    h.Username(builder.Configuration["RabbitMQ:Username"]);
                    h.Password(builder.Configuration["RabbitMQ:Password"]);
                });

                cfg.ReceiveEndpoint("session-revoked-users", e =>
                {
                    e.ConfigureConsumer<SessionRevokedConsumer>(context);
                });
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

        app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);
        app.Run();
    }
}
