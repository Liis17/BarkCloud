using Grpc.Core;
using Grpc.Core.Interceptors;

using System.Security.Cryptography;
using System.Text;

namespace BarkCloud.Configuration.Infrastructure;

/// <summary>
/// Гейт доступа к ConfigurationApi по предразделённому ключу <c>CONFIGURATION_ACCESS_KEY</c>.
/// Why: Configuration раздаёт секреты (включая JwtSettings:SecretKey) при старте каждого сервиса,
/// поэтому JWT-авторизация здесь невозможна — секрета у сервиса ещё нет (bootstrap «курица и яйцо»).
/// Ключ берётся из окружения (не из самого Configuration) и проверяется в постоянном времени.
/// Если ключ не задан — доступ открыт (для совместимости со старыми деплоями), но пишется warning.
/// </summary>
public sealed class ConfigurationAccessInterceptor : Interceptor
{
    public const string HeaderName = "x-config-access-key";

    private readonly byte[]? _expectedKey;
    private readonly ILogger<ConfigurationAccessInterceptor> _logger;
    private int _warned;

    public ConfigurationAccessInterceptor(IConfiguration configuration, ILogger<ConfigurationAccessInterceptor> logger)
    {
        var key = configuration["CONFIGURATION_ACCESS_KEY"];
        _expectedKey = string.IsNullOrEmpty(key) ? null : Encoding.UTF8.GetBytes(key);
        _logger = logger;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (_expectedKey is null)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                _logger.LogWarning(
                    "CONFIGURATION_ACCESS_KEY не задан — ConfigurationApi доступен без аутентификации. " +
                    "Задайте ключ в окружении всех сервисов, чтобы защитить раздачу секретов.");
            }

            return continuation(request, context);
        }

        var provided = context.RequestHeaders.GetValue(HeaderName);

        if (provided is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), _expectedKey))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid configuration access key"));
        }

        return continuation(request, context);
    }
}
