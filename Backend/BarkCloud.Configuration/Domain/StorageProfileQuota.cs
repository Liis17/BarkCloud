using System.Globalization;

namespace BarkCloud.Configuration.Domain;

public static class StorageProfileQuota
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private const long Terabyte = Gigabyte * 1024;
    private const long Petabyte = Terabyte * 1024;

    public static long ParseBytes(string value, string unit)
    {
        if (!long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            throw new InvalidOperationException("Квота должна быть целым неотрицательным числом.");

        var multiplier = unit.Trim().ToLowerInvariant() switch
        {
            "gb" => Gigabyte,
            "tb" => Terabyte,
            "pb" => Petabyte,
            _ => throw new InvalidOperationException("Единица квоты должна быть ГБ, ТБ или ПБ.")
        };
        try
        {
            return checked(amount * multiplier);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("Значение квоты превышает допустимый размер.", exception);
        }
    }
}
