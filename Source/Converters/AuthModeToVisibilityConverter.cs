using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PayloadPanda.Models;

namespace PayloadPanda.Converters;

public class AuthModeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AuthMode mode && parameter is string target)
        {
            return mode.ToString() == target ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
