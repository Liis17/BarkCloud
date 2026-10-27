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
    private IReadOnlyDictionary<byte[], ServiceId>? _registry;

    public override Task<GetServiceTokenResponse> GetServiceToken(
        GetServiceTokenRequest request,
        ServerCallContext context)
    {
        var registry = GetRegistry();

        var caller = MatchCaller(registry, request.Credential);
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

    private IReadOnlyDictionary<byte[], ServiceId> GetRegistry()
    {
        if (_registry is not null)
            return _registry;

        var raw = configuration["ServiceAuth:Credentials"]
                  ?? Environment.GetEnvironmentVariable("SERVICE_CREDENTIALS")
                  ?? string.Empty;

        var registry = new Dictionary<byte[], ServiceId>(ByteArrayComparer.Instance);
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                logger.LogWarning("Пропущена некорректная запись в ServiceAuth:Credentials (нет «Name=secret»)");
                continue;
            }

            var name = entry[..separator].Trim();
            var secret = entry[(separator + 1)..].Trim();
            if (secret.Length == 0)
            {
                logger.LogWarning("Пропущена запись сервиса {Name} с пустым секретом в ServiceAuth:Credentials", name);
                continue;
            }

            if (!System.Enum.TryParse<ServiceId>(name, ignoreCase: true, out var serviceId) || serviceId == ServiceId.Unknown)
            {
                logger.LogWarning("Неизвестное имя сервиса {Name} в ServiceAuth:Credentials — запись пропущена", name);
                continue;
            }

            registry[Encoding.UTF8.GetBytes(secret)] = serviceId;
        }

        if (registry.Count == 0)
            logger.LogWarning(
                "Реестр сервисных секретов (ServiceAuth:Credentials/SERVICE_CREDENTIALS) пуст — " +
                "выдача сервисных токенов невозможна (fail-closed).");

        Interlocked.CompareExchange(ref _registry, registry, null);
        return _registry;
    }

    private static ServiceId? MatchCaller(IReadOnlyDictionary<byte[], ServiceId> registry, string credential)
    {
        if (string.IsNullOrEmpty(credential))
            return null;

        var provided = Encoding.UTF8.GetBytes(credential);
        foreach (var (secret, serviceId) in registry)
        {
            if (CryptographicOperations.FixedTimeEquals(provided, secret))
                return serviceId;
        }

        return null;
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length == 0 ? 0 : HashCode.Combine(obj.Length, obj[0], obj[^1]);
    }
}
