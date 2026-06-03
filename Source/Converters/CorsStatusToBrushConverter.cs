using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PayloadPanda.Models;

namespace PayloadPanda.Converters;

public class CorsStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Yellow = new(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(0x8B, 0x94, 0x9E));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is CorsCheckStatus status)
        {
            return status switch
            {
                CorsCheckStatus.Pass => Green,
                CorsCheckStatus.Warn => Yellow,
                CorsCheckStatus.Fail => Red,
                _ => Gray
            };
        }
        return Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
