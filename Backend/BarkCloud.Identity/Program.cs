using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.ServiceAuth;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Consumers;
using BarkCloud.Identity.Host;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Configuration;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Auth;
using BarkCloud.Shared.Exceptions.Interceptors;
using BarkCloud.Shared.Identity;

using Fido2NetLib;

using MassTransit;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        await builder.LoadConfigurationAsync(ServiceId.Identity);
        await using var loggingLifetime = await builder.AddBarkCloudSerilogAsync("BarkCloud.Identity");
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
        // Origins хранится одной строкой (через запятую). Если пусто — выводим из RP ID.
        var webAuthnOriginsRaw = builder.Configuration["WebAuthn:Origins"];
        var webAuthnOrigins = string.IsNullOrWhiteSpace(webAuthnOriginsRaw)
            ? [$"https://{webAuthnRpId}"]
            : webAuthnOriginsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var androidCertificateFingerprint = builder.Configuration["BarkCloud:AndroidCertificateSha256Fingerprint"];
        if (!string.IsNullOrWhiteSpace(androidCertificateFingerprint))
        {
            var fingerprintHex = androidCertificateFingerprint
                .Replace(":", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal)
                .Trim();
            if (fingerprintHex.Length != 64 || !fingerprintHex.All(Uri.IsHexDigit))
                throw new InvalidOperationException(
                    "BarkCloud:AndroidCertificateSha256Fingerprint must be a 32-byte SHA-256 fingerprint in hexadecimal.");

            var apkKeyHash = Convert.ToBase64String(Convert.FromHexString(fingerprintHex))
                .TrimEnd('=')
                .Replace("+", "-", StringComparison.Ordinal)
                .Replace("/", "_", StringComparison.Ordinal);
            webAuthnOrigins = [.. webAuthnOrigins, $"android:apk-key-hash:{apkKeyHash}"];
        }

        builder.Services.AddSingleton<IFido2>(_ => new Fido2(new Fido2Configuration
        {
            ServerDomain = webAuthnRpId,
            ServerName = webAuthnServerName,
            Origins = new HashSet<string>(webAuthnOrigins)
        }));

        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

        builder.Services.AddXAuth(builder.Configuration, ServiceId.Identity);
        builder.Services.AddSingleton<IRevocationFeed, DbRevocationFeed>();

        builder.Services.AddCors(o => o.AddPolicy("IdentityCors", p =>
        {
            p.AllowAnyOrigin()
             .AllowAnyMethod()
             .AllowAnyHeader()
             .WithExposedHeaders("grpc-status", "grpc-message", "grpc-status-details-bin", "x-error-code");
        }));

        var configurationAddress = builder.Configuration["ConfigurationServiceAddr"]
                                   ?? Environment.GetEnvironmentVariable("CONFIGURATION_SERVICE_URL")
                                   ?? "http://localhost:7003";

        builder.Services.AddGrpcClient<ConfigurationApi.ConfigurationApiClient>(o =>
        {
            o.Address = new Uri(configurationAddress);
        });

        // Сервисные токены для server-API Users: короткоживущие, выдаёт ServiceCredentialsApi
        // по секрету сервиса (ServiceAuth:Credential / SERVICE_CREDENTIAL).
        builder.Services.AddServiceTokenClient(builder.Configuration);
        builder.Services.AddGrpcClient<UsersServerApi.UsersServerApiClient>(o =>
            {
                o.Address = new Uri(builder.Configuration["UsersService:Host"]);
            }).AddInterceptor(sp => new ServiceTokenClientInterceptor(
                sp.GetRequiredService<ServiceTokenProvider>(), ServiceId.Users))
            .AddInterceptor(() => new ExceptionClientInterceptor());

        builder.Services.AddTransient<IRefreshTokensStorage, RefreshTokensStorage>();
        builder.Services.AddSingleton<IRegistrationPolicy, RegistrationPolicy>();
        builder.Services.AddScoped<SessionIssuer>();
        builder.Services.AddScoped<PasswordChangedNotifier>();
        builder.Services.AddScoped<ReauthPasswordVerifier>();
        builder.Services.AddScoped<ReauthenticationService>();
        builder.Services.AddScoped<IAuthRateLimiter, AuthRateLimiter>();
        builder.Services.AddTransient<IAttemptCountersStorage, AttemptCountersStorage>();
        builder.Services.AddTransient<JwtService>();
        builder.Services.AddTransient<IConfirmationCodesStorage, ConfirmationCodesStorage>();
        builder.Services.AddScoped<NotificationQueueSender>();
        builder.Services.AddScoped<INotificationOutbox, NotificationOutbox>();
        builder.Services.AddHostedService<NotificationOutboxWorker>();
        builder.Services.AddHttpClient<LocationClient>();
        builder.Services.AddScoped<LocationClient>();
        builder.Services.AddTransient<IAuthPropertiesStorage, AuthPropertiesStorage>();
        builder.Services.AddTransient<IWebAuthnStorage, WebAuthnStorage>();
        builder.Services.AddTransient<IPasswordsStorage, PasswordsStorage>();
        builder.Services.AddTransient<IResetPasswordsStorage, ResetPasswordsStorage>();

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
        app.MapGrpcService<ServiceCredentialsApiService>();
        app.MapGrpcService<SessionRevocationApiService>();

        await app.RunAsync();

    }
}
