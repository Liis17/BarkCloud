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
    private const string TorrentKey = "TorrentEnabled";

    private readonly Metadata? _headers = BuildHeaders();
    private readonly Flag _registration = new(RegistrationKey, "регистрации", fallbackConfiguration.RegistrationEnabled);
    private readonly Flag _torrent = new(TorrentKey, "торрентов", fallbackConfiguration.TorrentEnabled);

    public Task<bool> RegistrationEnabledAsync(CancellationToken cancellationToken = default)
        => ReadAsync(_registration, cancellationToken);

    public Task<UpdateConfigurationResponse> SetRegistrationEnabledAsync(
        bool enabled,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
        => SetAsync(_registration, enabled, editedBy, editedFrom, cancellationToken);

    /// <summary>Читается на каждый запрос: меню и API Web меняются сразу, Torrent — после перезапуска.</summary>
    public Task<bool> TorrentEnabledAsync(CancellationToken cancellationToken = default)
        => ReadAsync(_torrent, cancellationToken);

    public Task<UpdateConfigurationResponse> SetTorrentEnabledAsync(
        bool enabled,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
        => SetAsync(_torrent, enabled, editedBy, editedFrom, cancellationToken);

    private async Task<bool> ReadAsync(Flag flag, CancellationToken cancellationToken)
    {
        try
        {
            var response = await configuration.GetConfigurationAsync(
                new GetConfigurationRequest { ServiceId = (int)ServiceId.Web },
                _headers,
                deadline: DateTime.UtcNow.AddSeconds(3),
                cancellationToken: cancellationToken);

            var item = response.Configurations.FirstOrDefault(c =>
                string.Equals(c.Section, Section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Key, flag.Key, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                flag.LastKnown = flag.Fallback();
                return flag.LastKnown;
            }

            if (bool.TryParse(item.Value, out var enabled))
            {
                flag.LastKnown = enabled;
                return enabled;
            }

            logger.LogWarning("Некорректное значение {Section}:{Key}: {Value}", Section, flag.Key, item.Value);
        }
        catch (RpcException ex)
        {
            logger.LogWarning("Не удалось прочитать флаг {Flag}: {Status} {Detail}", flag.Title, ex.StatusCode, ex.Status.Detail);
        }

        return flag.LastKnown;
    }

    private async Task<UpdateConfigurationResponse> SetAsync(
        Flag flag,
        bool enabled,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await configuration.UpdateConfigurationAsync(new UpdateConfigurationRequest
            {
                Section = Section,
                Key = flag.Key,
                Value = enabled ? "true" : "false",
                ServiceId = (int)ServiceId.Unknown,
                EditedBy = editedBy,
                EditedFrom = editedFrom
            }, _headers, cancellationToken: cancellationToken);

            if (response.Success)
                flag.LastKnown = enabled;

            return response;
        }
        catch (RpcException ex)
        {
            logger.LogWarning("Не удалось обновить флаг {Flag}: {Status} {Detail}", flag.Title, ex.StatusCode, ex.Status.Detail);
            return new UpdateConfigurationResponse
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(ex.Status.Detail)
                    ? $"Не удалось обновить настройку {flag.Title}"
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

    /// <summary>Последнее известное значение отдаётся, пока Configuration недоступен.</summary>
    private sealed class Flag(string key, string title, Func<bool> fallback)
    {
        public string Key { get; } = key;
        public string Title { get; } = title;
        public Func<bool> Fallback { get; } = fallback;
        public bool LastKnown { get; set; } = fallback();
    }
}
