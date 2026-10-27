using BarkCloud.Shared.Identity;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;

namespace BarkCloud.GrpcServer.XAuth;

public static class XAuthExtensions
{
    /// <summary>
    /// JWT-валидация RS256: подписывает только Identity (приватный ключ), здесь — только публичные ключи.
    /// HMAC отклонён на уровне разрешённых алгоритмов, поэтому старый общий секрет больше не работает.
    /// </summary>
    public static IServiceCollection AddXAuth(this IServiceCollection services, IConfiguration configuration, ServiceId serviceId)
    {
        // Ключи строим сразу, а не в лямбле AddJwtBearer: та выполняется лениво (на первом запросе),
        // а непригодный ключ должен останавливать сервис на старте с понятным сообщением.
        var publicKeys = RsaJwtKeys.LoadPublicKeyMap(
            configuration[RsaJwtKeys.PublicKeyConfigKey]
            ?? Environment.GetEnvironmentVariable(RsaJwtKeys.PublicKeyEnvName));

        services.AddSingleton<TokenRevocationCache>();
        services.AddHostedService<RevocationSyncService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(cfg =>
            {
                cfg.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    // Валидатор подбирает ключ по kid из заголовка; неизвестный kid → отказ.
                    IssuerSigningKeys = publicKeys.Values,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateIssuer = true,
                    ValidIssuer = configuration["JwtSettings:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = configuration["JwtSettings:Audience"],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };

                cfg.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Headers.TryGetValue("x-auth-token", out var token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal;
                        var tokenType = principal?.FindFirst(IdentityClaims.TokenType)?.Value;

                        if (tokenType == TokenType.User.ToString())
                        {
                            var userIdStr = principal?.FindFirst(IdentityClaims.UserId)?.Value;
                            var deviceId = principal?.FindFirst(IdentityClaims.DeviceId)?.Value;

                            if (long.TryParse(userIdStr, out var userId)
                                && !string.IsNullOrEmpty(deviceId))
                            {
                                var cache = context.HttpContext.RequestServices
                                    .GetRequiredService<TokenRevocationCache>();

                                // iat валидированного токена. Если не прочитать —
                                // MinValue (≤ любого отзыва), т.е. fail-safe «отозван».
                                var issuedAt = (context.SecurityToken as JsonWebToken)?.IssuedAt
                                    ?? (context.SecurityToken as JwtSecurityToken)?.IssuedAt
                                    ?? DateTime.MinValue;

                                long? sessionId = long.TryParse(
                                    principal?.FindFirst(IdentityClaims.SessionId)?.Value, out var sid) ? sid : null;

                                if (cache.IsRevoked(userId, deviceId, issuedAt, sessionId))
                                {
                                    context.Fail("Session has been revoked");
                                }
                            }
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            // Пользовательские RPC — только пользовательские токены: сервисный токен
            // больше не проходит политику User (аудит безопасности, п. 9).
            options.AddPolicy(nameof(TokenType.User),
                p => p.RequireClaim(IdentityClaims.TokenType, TokenType.User.ToString()));

            // Сервисный токен обязан быть выпущен именно для этого сервиса (x-target-service).
            options.AddPolicy(nameof(TokenType.Service),
                p => p.AddRequirements(new ServiceTokenRequirement(serviceId)));

            foreach (var permission in ServicePermissions.All)
            {
                options.AddPolicy(XAuthPolicies.ServicePermission(permission),
                    p => p.AddRequirements(new ServiceTokenRequirement(serviceId, permission)));
            }
        });

        services.AddSingleton<IAuthorizationHandler, ServiceTokenAuthorizationHandler>();

        services.AddHttpContextAccessor();
        services.AddScoped<UserContext>();

        return services;
    }

    public static IApplicationBuilder UseXAuth(this IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
