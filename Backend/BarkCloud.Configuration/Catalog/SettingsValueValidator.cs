using System.Globalization;

namespace BarkCloud.Configuration.Catalog;

public static class SettingsValueValidator
{
    public static string ValidateAndNormalize(SettingsCatalogEntry entry, string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return string.Empty;

        return entry.ValueKind switch
        {
            SettingValueKind.Boolean when bool.TryParse(normalized, out var boolean) => boolean ? "true" : "false",
            SettingValueKind.Boolean => throw new InvalidOperationException("Значение должно быть true или false."),
            SettingValueKind.Integer when int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                => ValidateInteger(entry, integer),
            SettingValueKind.Integer => throw new InvalidOperationException("Значение должно быть целым числом."),
            SettingValueKind.Url when Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                                      && uri.Scheme is "http" or "https" => normalized.TrimEnd('/'),
            SettingValueKind.Url => throw new InvalidOperationException("Значение должно быть абсолютным HTTP(S) URL."),
            _ => normalized
        };
    }

    private static string ValidateInteger(SettingsCatalogEntry entry, int value)
    {
        if (entry.Key is "Port" or "Http1Port" or "PeerPort")
        {
            if (value is < 1 or > 65535)
                throw new InvalidOperationException("Порт должен быть в диапазоне от 1 до 65535.");
        }
        else if (value <= 0)
        {
            throw new InvalidOperationException("Значение должно быть положительным целым числом.");
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
