using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PayloadPanda.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNotNull = value != null && (value is not string s || s.Length > 0);
        bool invert = parameter is string p && p == "Invert";
        return (isNotNull ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
