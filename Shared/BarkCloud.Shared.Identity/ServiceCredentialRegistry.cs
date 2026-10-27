namespace BarkCloud.Shared.Identity;

/// <summary>
/// Парсер реестра сервисных секретов формата «Name=secret;Name2=secret2» (имена = значения
/// <see cref="ServiceId"/>). Используется Identity (ServiceAuth:Credentials / SERVICE_CREDENTIALS)
/// и Configuration (CONFIGURATION_ACCESS_KEYS) — формат общий, значения у них независимые.
/// </summary>
public static class ServiceCredentialRegistry
{
    /// <summary>
    /// Разбирает реестр: пропускает пустые компоненты и неизвестные имена (сообщая об этом через
    /// <paramref name="onSkipped"/>), отклоняет записи без «=» и с пустым секретом.
    /// </summary>
    public static IReadOnlyList<(ServiceId Service, string Secret)> Parse(
        string? raw,
        Action<string>? onSkipped = null)
    {
        var result = new List<(ServiceId, string)>();

        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                onSkipped?.Invoke($"некорректная запись «{entry}» (нет «Name=secret»)");
                continue;
            }

            var name = entry[..separator].Trim();
            var secret = entry[(separator + 1)..].Trim();
            if (secret.Length == 0)
            {
                onSkipped?.Invoke($"пустой секрет у сервиса {name}");
                continue;
            }

            if (!Enum.TryParse<ServiceId>(name, ignoreCase: true, out var serviceId)
                || serviceId == ServiceId.Unknown)
            {
                onSkipped?.Invoke($"неизвестное имя сервиса {name}");
                continue;
            }

            result.Add((serviceId, secret));
        }

        return result;
    }
}
