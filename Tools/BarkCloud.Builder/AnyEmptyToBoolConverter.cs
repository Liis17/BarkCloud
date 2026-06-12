using System.Globalization;
using System.Windows.Data;

namespace BarkCloud.Builder;

/// <summary>true, если хотя бы одно из значений пустое — для показа предупреждения о сертификатах.</summary>
public sealed class AnyEmptyToBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Any(v => string.IsNullOrWhiteSpace(v as string));

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
