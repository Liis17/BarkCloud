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

using Fido2NetLib;

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

        // WebAuthn / FIDO2. RP ID = домен сервера (self-hosted: свой на инстанс), origins —
        // допустимые источники (Web-браузер и Drive через webauthn.dll шлют https://<RpId>).
        var webAuthnRpId = builder.Configuration["WebAuthn:RpId"] ?? "localhost";
        var webAuthnServerName = builder.Configuration["WebAuthn:ServerName"] ?? "BarkCloud";
        var webAuthnOrigins = builder.Configuration.GetSection("WebAuthn:Origins").Get<string[]>();
        if (webAuthnOrigins is null || webAuthnOrigins.Length == 0)
        {
            webAuthnOrigins = [$"https://{webAuthnRpId}"];
        }

        builder.Services.AddSingleton<IFido2>(_ => new Fido2(new Fido2Configuration
        {
            ServerDomain = webAuthnRpId,
            ServerName = webAuthnServerName,
            Origins = new HashSet<string>(webAuthnOrigins)
        }));

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

        builder.Services.AddTransient<IRefreshTokensStorage, RefreshTokensStorage>();
        builder.Services.AddScoped<SessionIssuer>();
        builder.Services.AddTransient<JwtService>();
        builder.Services.AddTransient<IConfirmationCodesStorage, ConfirmationCodesStorage>();
        builder.Services.AddScoped<NotificationQueueSender>();
        builder.Services.AddHttpClient<LocationClient>();
        builder.Services.AddScoped<LocationClient>();
        builder.Services.AddTransient<IAuthPropertiesStorage, AuthPropertiesStorage>();
        builder.Services.AddTransient<IWebAuthnStorage, WebAuthnStorage>();
        builder.Services.AddTransient<IPasswordsStorage, PasswordsStorage>();
        builder.Services.AddTransient<IResetPasswordsStorage, ResetPasswordsStorage>();

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