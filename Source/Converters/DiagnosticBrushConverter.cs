using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PayloadPanda.Converters;

/// <summary>
/// Maps diagnostic labels to theme accent brushes: the timing-waterfall phase names
/// (DNS / TCP / TLS / TTFB) and the certificate validity statuses (Valid / Expiring
/// Soon / Expired). Falls back to muted gray for anything unrecognised.
/// </summary>
public class DiagnosticBrushConverter : IValueConverter
{
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private static readonly SolidColorBrush Blue = Brush(0x58, 0xA6, 0xFF);
    private static readonly SolidColorBrush Purple = Brush(0xBC, 0x8C, 0xFF);
    private static readonly SolidColorBrush Green = Brush(0x3F, 0xB9, 0x50);
    private static readonly SolidColorBrush Orange = Brush(0xF0, 0x88, 0x3E);
    private static readonly SolidColorBrush Yellow = Brush(0xD2, 0x99, 0x22);
    private static readonly SolidColorBrush Red = Brush(0xF8, 0x51, 0x49);
    private static readonly SolidColorBrush Gray = Brush(0x8B, 0x94, 0x9E);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() switch
        {
            "DNS" => Blue,
            "TCP" => Purple,
            "TLS" => Green,
            "TTFB" => Orange,
            "Valid" => Green,
            "Expiring Soon" => Yellow,
            "Expired" => Red,
            _ => Gray
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
