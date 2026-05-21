using System.Globalization;
using System.Windows.Data;

namespace PayloadPanda.Converters;

/// <summary>
/// Two-way converter for binding a single enum-valued property to a set of mutually
/// exclusive RadioButtons. <c>Convert</c> returns true when the bound value matches the
/// ConverterParameter (enum member name); <c>ConvertBack</c> returns the matching enum
/// value when the RadioButton becomes checked, and ignores the uncheck.
/// </summary>
public class EnumMatchToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter != null
            ? Enum.Parse(targetType, parameter.ToString()!)
            : Binding.DoNothing;
}
