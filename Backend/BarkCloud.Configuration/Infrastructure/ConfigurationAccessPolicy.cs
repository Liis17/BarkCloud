namespace BarkCloud.Configuration.Infrastructure;

public static class ConfigurationAccessPolicy
{
    public static void EnsureConfigured(bool isDevelopment, string? accessKey)
    {
        if (!isDevelopment && string.IsNullOrWhiteSpace(accessKey))
            throw new InvalidOperationException(
                "CONFIGURATION_ACCESS_KEY обязателен вне Development. "
                + "Задайте одинаковый bootstrap-ключ в Configuration и всех сервисах-потребителях.");
    }
}
