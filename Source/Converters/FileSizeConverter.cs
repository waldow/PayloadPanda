using System.Globalization;
using System.Windows.Data;
using PayloadPanda.Services;

namespace PayloadPanda.Converters;

public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long size = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0
        };
        return ByteSize.Format(size);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
