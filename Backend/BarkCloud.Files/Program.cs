using BarkCloud.Files.Consumers;
using BarkCloud.Files.Extensions;
using BarkCloud.Files.Host;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Scheduling;
using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.SessionRevocation;
using BarkCloud.Shared.Auth;
using BarkCloud.Shared.Exceptions.Interceptors;
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

        // Тело запроса ограничено 32 МиБ (> 20 МиБ gRPC MaxReceiveMessageSize). Загрузки больше
        // задают свой лимит сами: part-PUT — [DisableRequestSizeLimit], legacy upload —
        // LegacyUploadAdmissionFilter. Минимальная скорость снята глобально (part-PUT на медленном
        // канале иначе оборвётся); для legacy upload её выставляет фильтр на запрос (только HTTP/1.x).
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            o.Limits.MinRequestBodyDataRate = null;
        });

        var legacyUploadOptions = builder.Configuration.GetSection("Uploads:Legacy").Get<LegacyUploadOptions>()
                                  ?? new LegacyUploadOptions();
        builder.Services.AddSingleton(legacyUploadOptions);
        builder.Services.AddSingleton<LegacyUploadBudget>();
        builder.Services.AddScoped<LegacyUploadAdmissionFilter>();

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
        builder.Services.AddSingleton<IRevocationFeed, GrpcRevocationFeed>();
        builder.Services.AddGrpcClient<SessionRevocationApi.SessionRevocationApiClient>(o =>
            {
                o.Address = new Uri(builder.Configuration["IdentityService:Host"]!);
            }).AddInterceptor(() => new JwtClientInterceptor(builder.Configuration["IdentityService:Token"]!))
            .AddInterceptor(() => new ExceptionClientInterceptor());
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
        builder.Services.AddSingleton<ImagePlaceholderSampler>();
        builder.Services.AddScoped<FilePlaceholderService>();
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
        builder.Services.AddSingleton<StorageMigrationGate>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ITrashPurgeService, TrashPurgeService>();
        builder.Services.AddSingleton<IPhysicalStorageStatsProvider, PhysicalStorageStatsProvider>();
        builder.Services.AddSingleton<IS3StorageStatsProvider, S3StorageStatsProvider>();
        builder.Services.AddHostedService<StorageStatsWarmupService>();
        builder.Services.AddHostedService<TempFileCleanupService>();
        builder.Services.AddHostedService<TrashCleanupService>();
        builder.Services.AddHostedService<OrphanBlobCleanupService>();
        builder.Services.AddHostedService<UploadSessionCleanupService>();
        builder.Services.AddHostedService<LegacyPreviewBackfillService>();
        builder.Services.AddHostedService<LegacyMetadataBackfillService>();
        builder.Services.AddHostedService<LegacyVideoHdrBackfillService>();
        builder.Services.AddHostedService<LegacyJpegViewBackfillService>();
        builder.Services.AddHostedService<FilePlaceholderBackfillService>();

        // Путь к бинарям ffmpeg/ffprobe в образе (см. Dockerfile). По умолчанию — /usr/local/bin.
        FFMpegCore.GlobalFFOptions.Configure(o =>
            o.BinaryFolder = builder.Configuration["Ffmpeg:BinaryFolder"] ?? "/usr/local/bin");

        builder.Services.AddMinioS3(builder.Configuration);

        builder.Services.AddDbContext<FilesContext>(options =>
            options.UseNpgsql(builder.Configuration["FilesDb"]));

        builder.Services.AddUploadScheduler(builder.Configuration["FilesDb"]!);

        builder.Services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<FilesContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            x.AddMessageScheduler(UploadProcessingQueue.SchedulerAddress);
            x.AddQuartzConsumers(o => o.QueueName = UploadProcessingQueue.SchedulerQueueName);

            x.AddConsumer<UserDeletedConsumer>();
            x.AddConsumer<ProcessUploadedFileConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(builder.Configuration["RabbitMQ:Host"], "/", h =>
                {
                    h.Username(builder.Configuration["RabbitMQ:Username"]);
                    h.Password(builder.Configuration["RabbitMQ:Password"]);
                });

                cfg.ReceiveEndpoint("user-deleted-files", e =>
                {
                    e.ConfigureConsumer<UserDeletedConsumer>(context);
                });

                cfg.ConfigureUploadScheduler(context);
                cfg.ReceiveEndpoint("process-uploaded-file", e => e.ConfigureUploadProcessing(context));
            });
        });

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<FilesContext>();
            ctx.Database.Migrate();
        }
        app.Services.GetRequiredService<StorageMigrationGate>().InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

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
