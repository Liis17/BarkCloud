using BarkCloud.Shared.Identity;

namespace BarkCloud.Configuration.Infrastructure;

public static class ConfigurationAccessPolicy
{
    public static void EnsureConfigured(bool isDevelopment, string? accessKeys)
    {
        if (!string.IsNullOrWhiteSpace(accessKeys))
        {
            var duplicates = ServiceCredentialRegistry.Parse(accessKeys)
                .GroupBy(entry => entry.Service)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            if (duplicates.Count > 0)
                throw new InvalidOperationException(
                    "CONFIGURATION_ACCESS_KEYS содержит дубликаты сервисов: "
                    + string.Join(", ", duplicates) + ". У каждого сервиса — один ключ.");

            return;
        }

        if (isDevelopment)
        {
            // Interceptor работает fail-closed и без реестра: API просто недоступен.
            return;
        }

        throw new InvalidOperationException(
            "CONFIGURATION_ACCESS_KEYS обязателен вне Development. "
            + "Задайте формат «Identity=key1;Users=key2;Files=key3;Torrent=key4;Web=key5;Notification=key6» "
            + "и раздайте каждому сервису его ключ через CONFIGURATION_ACCESS_KEY.");
    }
}
