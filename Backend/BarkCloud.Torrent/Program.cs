using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.ServiceAuth;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.SessionRevocation;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Interceptors;
using BarkCloud.Shared.Identity;
using BarkCloud.Torrent.Consumers;
using BarkCloud.Torrent.Host;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;

using MassTransit;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Torrent;

public class Program
{
    public static async Task Main(string[] args)
    {
        // gRPC к Files идёт по docker-сети без TLS (h2c) — разрешаем HTTP/2 поверх http://
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var builder = WebApplication.CreateBuilder(args);

        await builder.LoadConfigurationAsync(ServiceId.Torrent);
        await using var loggingLifetime = await builder.AddBarkCloudSerilogAsync("BarkCloud.Torrent");
        builder.SetRunningAddress(builder.Configuration);

        // Стриминг файлов без лимита размера тела и минимальной скорости.
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = null;
            o.Limits.MinRequestBodyDataRate = null;
        });

        // Флаг читается только при старте: изменение применяется перезапуском сервиса.
        var enabled = builder.Configuration.TorrentEnabled();
        AddTorrentServices(builder.Services, builder.Configuration, enabled);

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TorrentContext>();
            ctx.Database.Migrate();
        }

        if (enabled)
        {
            app.MapGrpcReflectionService();

            app.UseRouting();
            app.UseXAuth();

            app.MapControllers();
            app.MapGrpcService<TorrentApiService>();
        }
        else
        {
            app.Logger.LogInformation(
                "Торренты отключены (Features:TorrentEnabled=false): движок, API и фоновые задачи не запускаются");
        }

        await app.RunAsync();
    }

    /// <summary>
    /// Выключенный сервис держит только БД и consumer удаления аккаунта, чтобы данные удалённых
    /// пользователей очищались и без движка; API, движок, метрики и фоновые задачи не регистрируются.
    /// </summary>
    internal static void AddTorrentServices(IServiceCollection services, IConfiguration configuration, bool enabled)
    {
        // Выключенному сервису нужен только счётчик для consumer, без периодического цикла отправки метрик.
        if (enabled)
            services.AddBarkCloudMetrics("BarkCloud.Torrent");
        else
            services.AddSingleton<MetricsCollector>();

        services.AddDbContext<TorrentContext>(c =>
            c.UseNpgsql(configuration["TorrentDb"]));

        // Без TorrentStartupService движок не инициализируется; consumer обращается к нему
        // только для торрентов из реестра, который в этом режиме пуст.
        services.AddSingleton<TorrentEngineService>();

        services.AddMassTransit(x =>
        {
            x.AddConsumer<UserDeletedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(configuration["RabbitMQ:Host"], configuration["RabbitMQ:VirtualHost"], h =>
                {
                    h.Username(configuration["RabbitMQ:Username"]);
                    h.Password(configuration["RabbitMQ:Password"]);
                });
                cfg.OverrideDefaultBusEndpointQueueName("bark-torrent-bus-" + Guid.NewGuid().ToString("N"));

                cfg.ReceiveEndpoint("user-deleted-torrent", e =>
                {
                    e.ConfigureConsumer<UserDeletedConsumer>(context);
                });
            });
        });

        if (!enabled)
            return;

        services.AddBarkCloudGrpc();
        services.AddGrpcReflection();

        services.AddControllers();

        services.AddXAuth(configuration, ServiceId.Torrent);
        services.AddSingleton<IRevocationFeed, GrpcRevocationFeed>();

        // Сервисный токен Torrent — только чтение отзыва сессий (см. ServicePermissions).
        services.AddServiceTokenClient(configuration);
        services.AddGrpcClient<SessionRevocationApi.SessionRevocationApiClient>(o =>
            {
                o.Address = new Uri(configuration["IdentityService:Host"]!);
            }).AddInterceptor(sp => new ServiceTokenClientInterceptor(
                sp.GetRequiredService<ServiceTokenProvider>(), ServiceId.Identity))
            .AddInterceptor(() => new ExceptionClientInterceptor());

        services.AddScoped<ITorrentStore, TorrentStore>();
        services.AddScoped<TorrentImportService>();

        // gRPC-клиенты Files для импорта в облако (токен пользователя пробрасывается per-call).
        var filesAddress = configuration["FilesService:Host"];
        services.AddGrpcClient<FilesApi.FilesApiClient>(o => o.Address = new Uri(filesAddress!));
        services.AddGrpcClient<CloudApi.CloudApiClient>(o => o.Address = new Uri(filesAddress!));
        TorrentImportService.AddFilesUploadClient(services);

        services.AddHostedService<TorrentStartupService>();
        services.AddHostedService<TorrentPersistenceService>();
    }
}
