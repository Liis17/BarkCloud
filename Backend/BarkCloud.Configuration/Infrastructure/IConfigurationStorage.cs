using BarkCloud.Configuration.Domain;
using BarkCloud.Shared.Identity;

namespace BarkCloud.Configuration.Infrastructure;

public interface IConfigurationStorage
{
    Task<List<ConfigurationItem>> GetConfiguration(ServiceId serviceId);
    Task<bool> IsEmailConfiguredAsync();
    Task UpdateConfigurationAsync(string section, string key, string value, ServiceId serviceId, string editedBy, string editedFrom);
    Task<List<string>> GetReservedNamesAsync();
    Task AddReservedNameAsync(string name);
    Task UpdateReservedNameAsync(string oldName, string newName);
    Task DeleteReservedNameAsync(string name);
}
