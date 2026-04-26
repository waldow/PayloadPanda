using System.Globalization;
using System.Windows.Data;

namespace PayloadPanda.Converters;

public class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double size = value switch
        {
            long l => l,
            int i => i,
            double d => d,
            _ => 0
        };

        int unitIndex = 0;
        while (size >= 1024 && unitIndex < Units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size:F0} {Units[unitIndex]}"
            : $"{size:F1} {Units[unitIndex]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
