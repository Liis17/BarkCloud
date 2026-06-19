using BarkCloud.GrpcServer;
using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;

using Grpc.Core;

namespace BarkCloud.Identity.Infrastructure;

public interface IRegistrationPolicy
{
    Task EnsureRegistrationEnabledAsync(CancellationToken cancellationToken);
}

public sealed class RegistrationPolicy(
    ConfigurationApi.ConfigurationApiClient configuration,
    IConfiguration fallbackConfiguration,
    ILogger<RegistrationPolicy> logger) : IRegistrationPolicy
{
    private const string Section = "Features";
    private const string RegistrationKey = "RegistrationEnabled";

    private readonly Metadata? _headers = BuildHeaders();
    private bool _lastKnownRegistrationEnabled = fallbackConfiguration.RegistrationEnabled();

    public async Task EnsureRegistrationEnabledAsync(CancellationToken cancellationToken)
    {
        if (await RegistrationEnabledAsync(cancellationToken))
            return;

        logger.LogInformation("Регистрация новых аккаунтов отключена конфигурацией");
        throw new RegistrationDisabledException();
    }

    private async Task<bool> RegistrationEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await configuration.GetConfigurationAsync(
                new GetConfigurationRequest { ServiceId = (int)ServiceId.Identity },
                _headers,
                cancellationToken: cancellationToken);

            var item = response.Configurations.FirstOrDefault(c =>
                string.Equals(c.Section, Section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Key, RegistrationKey, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                _lastKnownRegistrationEnabled = fallbackConfiguration.RegistrationEnabled();
                return _lastKnownRegistrationEnabled;
            }

            if (bool.TryParse(item.Value, out var enabled))
            {
                _lastKnownRegistrationEnabled = enabled;
                return enabled;
            }

            logger.LogWarning("Некорректное значение {Section}:{Key}: {Value}", Section, RegistrationKey, item.Value);
        }
        catch (RpcException ex)
        {
            logger.LogWarning("Не удалось прочитать флаг регистрации: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
        }

        return _lastKnownRegistrationEnabled;
    }

    private static Metadata? BuildHeaders()
    {
        var accessKey = Environment.GetEnvironmentVariable("CONFIGURATION_ACCESS_KEY");
        return string.IsNullOrEmpty(accessKey)
            ? null
            : new Metadata { { "x-config-access-key", accessKey } };
    }
}