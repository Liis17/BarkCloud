using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Identity;

using Grpc.Core;
using Grpc.Core.Interceptors;

using System.Security.Cryptography;
using System.Text;

namespace BarkCloud.Configuration.Infrastructure;

/// <summary>
/// Гейт доступа к ConfigurationApi по предразделённым bootstrap-ключам сервисов
/// (env <c>CONFIGURATION_ACCESS_KEYS</c>, формат «Identity=key1;Users=key2;…»).
/// Каждый сервис знает только свой ключ (<c>CONFIGURATION_ACCESS_KEY</c> в его окружении) —
/// получить настройки чужого раздела или изменить их он не может. Административные операции
/// (история, изменение, откат, S3-профили, квоты, резервные имена) доступны только Web.
/// Пустой или неизвестный ключ отклоняется всегда (fail-closed; аудит безопасности, п. 9).
/// </summary>
public sealed class ConfigurationAccessInterceptor : Interceptor
{
    public const string HeaderName = "x-config-access-key";
    public const string RegistryEnvName = "CONFIGURATION_ACCESS_KEYS";

    private readonly byte[][] _secrets;
    private readonly ServiceId[] _services;
    private readonly ILogger<ConfigurationAccessInterceptor> _logger;
    private int _warnedEmptyRegistry;

    public ConfigurationAccessInterceptor(IConfiguration configuration, ILogger<ConfigurationAccessInterceptor> logger)
    {
        var raw = configuration["CONFIGURATION_ACCESS_KEYS"]
                  ?? Environment.GetEnvironmentVariable(RegistryEnvName);

        var registry = ServiceCredentialRegistry.Parse(raw,
            reason => logger.LogWarning("Пропущена запись реестра CONFIGURATION_ACCESS_KEYS: {Reason}", reason));

        _secrets = registry.Select(entry => Encoding.UTF8.GetBytes(entry.Secret)).ToArray();
        _services = registry.Select(entry => entry.Service).ToArray();
        _logger = logger;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (_secrets.Length == 0)
        {
            if (Interlocked.Exchange(ref _warnedEmptyRegistry, 1) == 0)
            {
                _logger.LogWarning(
                    "Реестр CONFIGURATION_ACCESS_KEYS пуст — ConfigurationApi недоступен (fail-closed). " +
                    "Задайте формат «Identity=key1;Users=key2;…» и раздайте сервисам их ключи.");
            }

            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid configuration access key"));
        }

        var provided = context.RequestHeaders.GetValue(HeaderName);
        var caller = MatchCaller(provided);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid configuration access key"));

        if (!IsAllowed(context.Method, caller.Value, request))
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                $"Service {caller.Value} is not allowed to call {context.Method}"));

        return continuation(request, context);
    }

    private ServiceId? MatchCaller(string? provided)
    {
        if (string.IsNullOrEmpty(provided))
            return null;

        var bytes = Encoding.UTF8.GetBytes(provided);
        for (var i = 0; i < _secrets.Length; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(bytes, _secrets[i]))
                return _services[i];
        }

        return null;
    }

    /// <summary>Право на конкретный RPC; всё нераспознанное запрещено (default deny).</summary>
    private static bool IsAllowed<TRequest>(string method, ServiceId caller, TRequest request)
    {
        const string prefix = "/" + "barkcloud.configuration.ConfigurationApi" + "/";

        if (!method.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rpc = method[prefix.Length..];

        return rpc switch
        {
            // Bootstrap: готовность настроек и чтение собственного раздела (+ общего глобального).
            nameof(ConfigurationApi.ConfigurationApiBase.GetReadiness) => true,
            nameof(ConfigurationApi.ConfigurationApiBase.GetConfiguration)
                when request is GetConfigurationRequest configuration
                => configuration.ServiceId == 0
                   || configuration.ServiceId == (int)caller,
            // Files читает S3-профили при старте; Web — в разделе настроек и миграции.
            nameof(ConfigurationApi.ConfigurationApiBase.GetStorageProfiles)
                => caller is ServiceId.Web or ServiceId.Files,
            // Административные операции — только доверенному Web.
            _ => caller == ServiceId.Web
        };
    }
}
