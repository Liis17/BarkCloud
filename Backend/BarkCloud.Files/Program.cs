using BarkCloud.Files.Consumers;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Host;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Identity;

using MassTransit;

using Microsoft.EntityFrameworkCore;

using Serilog;

namespace BarkCloud.Files;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.LoadConfiguration(ServiceId.Files);
        builder.AddBarkCloudSerilog("BarkCloud.Files");
        builder.SetRunningAddress(builder.Configuration);

        // Загрузка/скачивание файлов без лимита размера тела и без ограничения минимальной
        // скорости (HTTP1-эндпоинт 7026: иначе большой файл на медленном канале оборвётся).
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = null;
            o.Limits.MinRequestBodyDataRate = null;
        });

        // Регистрируем gRPC сервисы с интерцепторами
        builder.Services.AddGrpc(options =>
        {
            options.Interceptors.Add<ServerExceptionInterceptor>();
            options.Interceptors.Add<RequestContextInterceptor>();
            // Оригинальные файлы изображений от админ-панели могут быть больше дефолтных 4 МБ
            options.MaxReceiveMessageSize = 20 * 1024 * 1024; // 20 МБ
            options.MaxSendMessageSize = 20 * 1024 * 1024;    // 20 МБ
        });
        builder.Services.AddBarkCloudMetrics("BarkCloud.Files");

        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

        builder.Services.AddGrpcReflection();

        builder.Services.AddXAuth(builder.Configuration);
        builder.Services.AddRequestContext();

        // Регистрируем gRPC клиент для UsersServerApi
        builder.Services.AddGrpcClient<BarkCloud.Proto.Users.UsersServerApi.UsersServerApiClient>(o =>
            {
                o.Address = new Uri(builder.Configuration["UsersService:Host"]);
            }).AddInterceptor(() => new BarkCloud.Shared.Auth.JwtClientInterceptor(builder.Configuration["UsersService:Token"]))
            .AddInterceptor(() => new BarkCloud.Shared.Exceptions.Interceptors.ExceptionClientInterceptor());

        builder.Services.AddControllers();

        builder.Services.AddScoped<IUploadedFilesStorage, UploadedFilesStorage>();
        builder.Services.AddScoped<ITempFilesStorage, TempFilesStorage>();
        builder.Services.AddScoped<IFileHashesStorage, FileHashesStorage>();
        builder.Services.AddScoped<ICloudHierarchyStorage, CloudHierarchyStorage>();
        builder.Services.AddScoped<IAlbumStorage, AlbumStorage>();
        builder.Services.AddScoped<IDynamicFolderStorage, DynamicFolderStorage>();
        builder.Services.AddScoped<IFavoriteFilesStorage, FavoriteFilesStorage>();
        builder.Services.AddScoped<IShareStorage, ShareStorage>();
        builder.Services.AddScoped<IFolderShareStorage, FolderShareStorage>();
        builder.Services.AddScoped<IAlbumShareStorage, AlbumShareStorage>();
        builder.Services.AddScoped<IGrantStorage, GrantStorage>();
        builder.Services.AddScoped<IDirectoryGrantStorage, DirectoryGrantStorage>();
        builder.Services.AddScoped<IFileActivityStorage, FileActivityStorage>();
        builder.Services.AddScoped<FolderGrantAccessService>();
        builder.Services.AddScoped<IFileMetadataStorage, FileMetadataStorage>();
        builder.Services.AddScoped<FileActivityWriter>();
        builder.Services.AddSingleton<ImageCompressor>();
        builder.Services.AddSingleton<VideoThumbnailExtractor>();
        builder.Services.AddSingleton<AudioMetadataExtractor>();
        builder.Services.AddSingleton<HeicImageConverter>();
        builder.Services.AddSingleton<FileMetadataExtractor>();
        builder.Services.AddScoped<PreviewPersistenceService>();
        builder.Services.AddScoped<AlbumViewBuilder>();
        builder.Services.AddScoped<DynamicFolderViewBuilder>();
        builder.Services.AddScoped<MusicLibraryService>();
        builder.Services.AddScoped<UnifiedSearchService>();
        builder.Services.AddScoped<UploadSessionCoordinator>();
        builder.Services.AddScoped<IUploadProcessingPublisher, MassTransitUploadProcessingPublisher>();
        builder.Services.AddScoped<UploadSessionProcessor>();
        builder.Services.AddScoped<IUploadEnrichmentPipeline, ExistingUploadEnrichmentPipeline>();
        builder.Services.AddScoped<IUploadArtifactCleaner, UploadArtifactCleaner>();
        builder.Services.AddSingleton<IUploadTempFileProvider, UploadTempFileProvider>();
        builder.Services.AddScoped<IStorageLimitProvider, UsersStorageLimitProvider>();
        builder.Services.AddScoped<IStorageQuotaService, StorageQuotaService>();
        builder.Services.AddScoped<LegacyUploadQuotaGuard>();
        builder.Services.AddScoped<ILegacyUploadCompletionMarker>(services =>
            services.GetRequiredService<LegacyUploadQuotaGuard>());
        builder.Services.AddScoped<UploadSessionMaintenance>();
        builder.Services.AddSingleton<IMultipartUploadStore, S3MultipartUploadStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ITrashPurgeService, TrashPurgeService>();
        builder.Services.AddSingleton<IPhysicalStorageStatsProvider, PhysicalStorageStatsProvider>();
        builder.Services.AddHostedService<TempFileCleanupService>();
        builder.Services.AddHostedService<TrashCleanupService>();
        builder.Services.AddHostedService<OrphanBlobCleanupService>();
        builder.Services.AddHostedService<UploadSessionCleanupService>();
        builder.Services.AddHostedService<LegacyPreviewBackfillService>();
        builder.Services.AddHostedService<LegacyMetadataBackfillService>();
        builder.Services.AddHostedService<LegacyVideoHdrBackfillService>();
        builder.Services.AddHostedService<LegacyJpegViewBackfillService>();

        // Путь к бинарям ffmpeg/ffprobe в образе (см. Dockerfile). По умолчанию — /usr/local/bin.
        FFMpegCore.GlobalFFOptions.Configure(o =>
            o.BinaryFolder = builder.Configuration["Ffmpeg:BinaryFolder"] ?? "/usr/local/bin");

        builder.Services.AddMinioS3(builder.Configuration);

        builder.Services.AddDbContext<FilesContext>(options =>
            options.UseNpgsql(builder.Configuration["FilesDb"]));

        builder.Services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<FilesContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            x.AddConsumer<SessionRevokedConsumer>();
            x.AddConsumer<UserDeletedConsumer>();
            x.AddConsumer<ProcessUploadedFileConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(builder.Configuration["RabbitMQ:Host"], "/", h =>
                {
                    h.Username(builder.Configuration["RabbitMQ:Username"]);
                    h.Password(builder.Configuration["RabbitMQ:Password"]);
                });

                cfg.ReceiveEndpoint("session-revoked-files", e =>
                {
                    e.ConfigureConsumer<SessionRevokedConsumer>(context);
                });

                cfg.ReceiveEndpoint("user-deleted-files", e =>
                {
                    e.ConfigureConsumer<UserDeletedConsumer>(context);
                });

                cfg.ReceiveEndpoint("process-uploaded-file", e =>
                {
                    // Bus outbox covers Complete + publish. Processing itself must not hold an EF
                    // transaction during S3 download/ffmpeg; the session state makes redelivery idempotent.
                    e.ConcurrentMessageLimit = 2;
                    e.UseMessageRetry(r => r.Intervals(
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(5),
                        TimeSpan.FromMinutes(15)));
                    e.ConfigureConsumer<ProcessUploadedFileConsumer>(context);
                });
            });
        });

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<FilesContext>();
            ctx.Database.Migrate();
        }

        app.MapGrpcReflectionService();

        app.UseXAuth();

        app.MapControllers();

        app.MapGrpcService<FilesApiService>();
        app.MapGrpcService<FilesServerApiService>();
        app.MapGrpcService<CloudApiService>();
        app.MapGrpcService<AlbumApiService>();
        app.MapGrpcService<MusicApiService>();
        app.MapGrpcService<DynamicFolderApiService>();
        app.MapGrpcService<SearchApiService>();

        app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);
        app.Run();
    }
}
