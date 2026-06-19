using BarkCloud.GrpcServer;
using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Identity;

using Grpc.Core;

namespace BarkCloud.Web.Infrastructure;

public sealed class FeatureConfigurationGateway(
    ConfigurationApi.ConfigurationApiClient configuration,
    IConfiguration fallbackConfiguration,
    ILogger<FeatureConfigurationGateway> logger)
{
    private const string Section = "Features";
    private const string RegistrationKey = "RegistrationEnabled";

    private readonly Metadata? _headers = BuildHeaders();
    private bool _lastKnownRegistrationEnabled = fallbackConfiguration.RegistrationEnabled();

    public async Task<bool> RegistrationEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await configuration.GetConfigurationAsync(
                new GetConfigurationRequest { ServiceId = (int)ServiceId.Web },
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

    public async Task<UpdateConfigurationResponse> SetRegistrationEnabledAsync(
        bool enabled,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await configuration.UpdateConfigurationAsync(new UpdateConfigurationRequest
            {
                Section = Section,
                Key = RegistrationKey,
                Value = enabled ? "true" : "false",
                ServiceId = (int)ServiceId.Unknown,
                EditedBy = editedBy,
                EditedFrom = editedFrom
            }, _headers, cancellationToken: cancellationToken);

            if (response.Success)
                _lastKnownRegistrationEnabled = enabled;

            return response;
        }
        catch (RpcException ex)
        {
            logger.LogWarning("Не удалось обновить флаг регистрации: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
            return new UpdateConfigurationResponse
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(ex.Status.Detail)
                    ? "Не удалось обновить настройку регистрации"
                    : ex.Status.Detail
            };
        }
    }

    private static Metadata? BuildHeaders()
    {
        var accessKey = Environment.GetEnvironmentVariable("CONFIGURATION_ACCESS_KEY");
        return string.IsNullOrEmpty(accessKey)
            ? null
            : new Metadata { { "x-config-access-key", accessKey } };
    }
}