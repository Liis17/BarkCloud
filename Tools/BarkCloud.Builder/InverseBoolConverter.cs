using System.Globalization;
using System.Windows.Data;

namespace BarkCloud.Builder;

/// <summary>Инвертирует bool — для показа предупреждения, когда тумблер выключен.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
