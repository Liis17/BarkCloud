using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using System.Security.Cryptography;
using System.Text;

namespace BarkCloud.Identity.Host;

/// <summary>
/// Обмен секрета сервиса на короткоживущий сервисный JWT. Аутентификация — сам credential:
/// реестр секретов (ServiceAuth:Credentials / env SERVICE_CREDENTIALS, «Name=secret;…»)
/// известен только Identity. Личность и разрешения определяются реестром, клиент их не выбирает.
/// </summary>
public class ServiceCredentialsApiService(
    IConfiguration configuration,
    JwtService jwtService,
    ILogger<ServiceCredentialsApiService> logger) : ServiceCredentialsApi.ServiceCredentialsApiBase
{
    private readonly (ServiceId Service, string Secret)[] _registry = ParseRegistry(configuration, logger);

    public override Task<GetServiceTokenResponse> GetServiceToken(
        GetServiceTokenRequest request,
        ServerCallContext context)
    {
        var caller = MatchCaller(request.Credential);
        if (caller is null)
        {
            // Детали не раскрываем: и неизвестный сервис, и неверный секрет — одинаково.
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid service credential"));
        }

        if (!System.Enum.IsDefined(typeof(ServiceId), request.TargetService)
            || (ServiceId)request.TargetService == ServiceId.Unknown)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown target service"));
        }

        var (token, expiresAt) = jwtService.GenerateServiceToken(caller.Value, (ServiceId)request.TargetService);

        return Task.FromResult(new GetServiceTokenResponse
        {
            Token = token,
            ExpiresAt = Timestamp.FromDateTime(expiresAt.UtcDateTime)
        });
    }

    private static (ServiceId Service, string Secret)[] ParseRegistry(IConfiguration configuration, ILogger logger)
    {
        var raw = configuration["ServiceAuth:Credentials"]
                  ?? Environment.GetEnvironmentVariable("SERVICE_CREDENTIALS")
                  ?? string.Empty;

        var registry = ServiceCredentialRegistry.Parse(raw,
            reason => logger.LogWarning("Пропущена запись реестра сервисных секретов: {Reason}", reason))
            .ToArray();

        if (registry.Length == 0)
            logger.LogWarning(
                "Реестр сервисных секретов (ServiceAuth:Credentials/SERVICE_CREDENTIALS) пуст — " +
                "выдача сервисных токенов невозможна (fail-closed).");

        return registry;
    }

    private ServiceId? MatchCaller(string credential)
    {
        if (string.IsNullOrEmpty(credential))
            return null;

        var provided = Encoding.UTF8.GetBytes(credential);
        foreach (var (service, secret) in _registry)
        {
            if (CryptographicOperations.FixedTimeEquals(provided, Encoding.UTF8.GetBytes(secret)))
                return service;
        }

        return null;
    }
}
