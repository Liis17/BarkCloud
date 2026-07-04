using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;
using BarkCloud.Torrent.Consumers;
using BarkCloud.Torrent.Host;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;

using MassTransit;

using Microsoft.EntityFrameworkCore;

using Serilog;

namespace BarkCloud.Torrent;

public class Program
{
    public static void Main(string[] args)
    {
        // gRPC к Files идёт по docker-сети без TLS (h2c) — разрешаем HTTP/2 поверх http://
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var builder = WebApplication.CreateBuilder(args);

        builder.LoadConfiguration(ServiceId.Torrent);
        builder.AddBarkCloudSerilog("BarkCloud.Torrent");
        builder.SetRunningAddress(builder.Configuration);

        // Стриминг файлов без лимита размера тела и минимальной скорости.
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = null;
            o.Limits.MinRequestBodyDataRate = null;
        });

        builder.Services.AddBarkCloudGrpc();
        builder.Services.AddBarkCloudMetrics("BarkCloud.Torrent");
        builder.Services.AddGrpcReflection();

        builder.Services.AddControllers();

        builder.Services.AddXAuth(builder.Configuration);

        builder.Services.AddDbContext<TorrentContext>(c =>
            c.UseNpgsql(builder.Configuration["TorrentDb"]));

        builder.Services.AddScoped<ITorrentStore, TorrentStore>();
        builder.Services.AddSingleton<TorrentEngineService>();
        builder.Services.AddScoped<TorrentImportService>();

        // gRPC-клиенты Files для импорта в облако (токен пользователя пробрасывается per-call).
        var filesAddress = builder.Configuration["FilesService:Host"];
        builder.Services.AddGrpcClient<FilesApi.FilesApiClient>(o => o.Address = new Uri(filesAddress!));
        builder.Services.AddGrpcClient<CloudApi.CloudApiClient>(o => o.Address = new Uri(filesAddress!));
        builder.Services.AddHttpClient("files-upload");

        builder.Services.AddHostedService<TorrentStartupService>();
        builder.Services.AddHostedService<TorrentPersistenceService>();

        builder.Services.AddMassTransit(x =>
        {
            x.AddConsumer<UserDeletedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(builder.Configuration["RabbitMQ:Host"], "/", h =>
                {
                    h.Username(builder.Configuration["RabbitMQ:Username"]);
                    h.Password(builder.Configuration["RabbitMQ:Password"]);
                });

                cfg.ReceiveEndpoint("user-deleted-torrent", e =>
                {
                    e.ConfigureConsumer<UserDeletedConsumer>(context);
                });
            });
        });

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TorrentContext>();
            ctx.Database.Migrate();
        }

        app.MapGrpcReflectionService();

        app.UseRouting();
        app.UseXAuth();

        app.MapControllers();
        app.MapGrpcService<TorrentApiService>();

        app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);
        app.Run();
    }
}
