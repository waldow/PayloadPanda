using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PayloadPanda.Converters;

public class StatusCodeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x58, 0xA6, 0xFF));
    private static readonly SolidColorBrush Yellow = new(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(0x8B, 0x94, 0x9E));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int statusCode)
        {
            return statusCode switch
            {
                >= 200 and < 300 => Green,
                >= 300 and < 400 => Blue,
                >= 400 and < 500 => Yellow,
                >= 500 => Red,
                _ => Gray
            };
        }
        return Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
