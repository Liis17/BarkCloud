using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Consumers;
using BarkCloud.Identity.Host;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Auth;
using BarkCloud.Shared.Exceptions.Interceptors;
using BarkCloud.Shared.Identity;

using MassTransit;

using Microsoft.EntityFrameworkCore;

using Serilog;

namespace BarkCloud.Identity;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.LoadConfiguration(ServiceId.Identity);
        builder.AddBarkCloudSerilog("BarkCloud.Identity");
        builder.SetRunningAddress(builder.Configuration);

        builder.Services.AddBarkCloudGrpc();
        builder.Services.AddBarkCloudMetrics("BarkCloud.Identity");
        builder.Services.AddGrpcReflection();

        builder.Services.AddDbContext<IdentityContext>(c
            => c.UseNpgsql(builder.Configuration["IdentityDb"]));

        builder.Services.AddSettings<JwtSettings>(builder.Configuration, "JwtSettings");

        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

        builder.Services.AddXAuth(builder.Configuration);

        builder.Services.AddCors(o => o.AddPolicy("IdentityCors", p =>
        {
            p.AllowAnyOrigin()
             .AllowAnyMethod()
             .AllowAnyHeader()
             .WithExposedHeaders("grpc-status", "grpc-message", "grpc-status-details-bin", "x-error-code");
        }));

        builder.Services.AddGrpcClient<UsersServerApi.UsersServerApiClient>(o =>
            {
                o.Address = new Uri(builder.Configuration["UsersService:Host"]);
            }).AddInterceptor(() => new JwtClientInterceptor(builder.Configuration["UsersService:Token"]))
            .AddInterceptor(() => new ExceptionClientInterceptor());

        builder.Services.AddTransient<RefreshTokensStorage>();
        builder.Services.AddTransient<JwtService>();
        builder.Services.AddTransient<ConfirmationCodesStorage>();
        builder.Services.AddScoped<NotificationQueueSender>();
        builder.Services.AddHttpClient<LocationClient>();
        builder.Services.AddScoped<LocationClient>();
        builder.Services.AddTransient<AuthPropertiesStorage>();
        builder.Services.AddTransient<PasswordsStorage>();
        builder.Services.AddTransient<ResetPasswordsStorage>();

        builder.Services.AddMassTransit(x =>
        {
            x.AddConsumer<SessionRevokedConsumer>();
            x.AddConsumer<UserDeletedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(builder.Configuration["RabbitMQ:Host"], "/", h =>
                {
                    h.Username(builder.Configuration["RabbitMQ:Username"]);
                    h.Password(builder.Configuration["RabbitMQ:Password"]);
                });

                cfg.ReceiveEndpoint("session-revoked-identity", e =>
                {
                    e.ConfigureConsumer<SessionRevokedConsumer>(context);
                });

                cfg.ReceiveEndpoint("user-deleted-identity", e =>
                {
                    e.ConfigureConsumer<UserDeletedConsumer>(context);
                });
            });
        });

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            ctx.Database.Migrate();
        }

        var metrics = app.Services.GetRequiredService<MetricsCollector>();
        metrics.Set("service_started_unix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        app.UseRouting();
        app.UseCors("IdentityCors");
        app.UseGrpcWeb();
        app.MapGrpcReflectionService();

        app.UseXAuth();

        app.MapGrpcService<IdentityApiService>().EnableGrpcWeb();
        app.MapGrpcService<IdentityServerApiService>();

        app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);
        app.Run();

    }
}