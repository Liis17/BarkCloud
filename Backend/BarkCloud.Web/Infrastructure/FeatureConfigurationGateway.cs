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
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private Task? _refresh;
    private DateTime _refreshStartedAt;
    private int _version;

    private IEnumerable<Flag> Flags => [_registration, _torrent];

    /// <summary>Читается заново на каждый запрос: выключенная регистрация действует сразу.</summary>
    public Task<bool> RegistrationEnabledAsync(CancellationToken cancellationToken = default)
        => ReadAsync(_registration, fresh: true, cancellationToken);

    public Task<UpdateConfigurationResponse> SetRegistrationEnabledAsync(
        bool enabled,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
        => SetAsync(_registration, enabled, editedBy, editedFrom, cancellationToken);

    /// <summary>Снимок до <see cref="CacheTtl"/>: меню и API Web меняются без перезапуска Web, Torrent — после своего.</summary>
    public Task<bool> TorrentEnabledAsync(CancellationToken cancellationToken = default)
        => ReadAsync(_torrent, fresh: false, cancellationToken);

    public Task<UpdateConfigurationResponse> SetTorrentEnabledAsync(
        bool enabled,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default)
        => SetAsync(_torrent, enabled, editedBy, editedFrom, cancellationToken);

    /// <summary>
    /// Флаг торрентов читается на горячих путях (каркас, поиск, API торрентов), поэтому один снимок
    /// Features на <see cref="CacheTtl"/> обслуживает параллельные запросы; <paramref name="fresh"/> его обходит.
    /// </summary>
    private async Task<bool> ReadAsync(Flag flag, bool fresh, CancellationToken cancellationToken)
    {
        Task refresh;
        lock (_gate)
        {
            if (fresh || _refresh is null || (_refresh.IsCompleted && DateTime.UtcNow - _refreshStartedAt >= CacheTtl))
            {
                _refreshStartedAt = DateTime.UtcNow;
                _refresh = RefreshAsync(_version);
            }
            refresh = _refresh;
        }

        await refresh.WaitAsync(cancellationToken);
        return flag.LastKnown;
    }

    private async Task RefreshAsync(int version)
    {
        try
        {
            var response = await configuration.GetConfigurationAsync(
                new GetConfigurationRequest { ServiceId = (int)ServiceId.Web },
                _headers,
                deadline: DateTime.UtcNow.AddSeconds(3));

            lock (_gate)
            {
                // Значение, сохранённое во время чтения, новее снимка.
                if (version != _version)
                    return;

                foreach (var flag in Flags)
                {
                    var item = response.Configurations.FirstOrDefault(c =>
                        string.Equals(c.Section, Section, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Key, flag.Key, StringComparison.OrdinalIgnoreCase));

                    if (item is null)
                        flag.LastKnown = flag.Fallback();
                    else if (bool.TryParse(item.Value, out var enabled))
                        flag.LastKnown = enabled;
                    else
                        logger.LogWarning("Некорректное значение {Section}:{Key}: {Value}", Section, flag.Key, item.Value);
                }
            }
        }
        catch (RpcException ex)
        {
            // До истечения TTL недоступный Configuration не опрашивается; отдаём последние значения.
            logger.LogWarning("Не удалось прочитать флаги Features: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
        }
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
            {
                lock (_gate)
                {
                    _version++;
                    flag.LastKnown = enabled;
                }
            }

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
