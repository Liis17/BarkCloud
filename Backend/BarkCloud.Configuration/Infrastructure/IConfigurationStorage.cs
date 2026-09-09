using BarkCloud.Configuration.Domain;
using BarkCloud.Shared.Identity;

namespace BarkCloud.Configuration.Infrastructure;

public interface IConfigurationStorage
{
    Task<List<ConfigurationItem>> GetConfiguration(ServiceId serviceId);
    Task<List<ConfigurationItem>> GetAllAsync();
    Task<bool> IsEmailConfiguredAsync();
    Task SeedMissingCatalogRowsAsync(
        Func<Catalog.SettingsCatalogEntry, string?> valueFactory,
        CancellationToken cancellationToken = default);
    Task UpdateConfigurationAsync(string section, string key, string value, ServiceId serviceId, string editedBy, string editedFrom);
    Task<IReadOnlyList<SettingRevision>> GetHistoryAsync(
        string section,
        string key,
        ServiceId serviceId,
        int count,
        CancellationToken cancellationToken = default);
    Task RollbackAsync(
        long revisionId,
        string editedBy,
        string editedFrom,
        CancellationToken cancellationToken = default);
    Task<List<string>> GetReservedNamesAsync();
    Task AddReservedNameAsync(string name);
    Task UpdateReservedNameAsync(string oldName, string newName);
    Task DeleteReservedNameAsync(string name);
}
