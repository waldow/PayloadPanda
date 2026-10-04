using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PayloadPanda.Converters;

/// <summary>
/// Visible when the bound enum value's name is in the comma-separated ConverterParameter
/// (e.g. "Raw,Json,Xml"); a leading "!" inverts it. Drives the per-body-mode panels.
/// </summary>
public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var spec = parameter as string ?? string.Empty;
        var invert = spec.StartsWith('!');
        var names = spec.TrimStart('!').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = value is not null && names.Contains(value.ToString());
        return matches ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
