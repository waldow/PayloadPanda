namespace PayloadPanda.Services;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB"];

    /// <summary>Human-readable size: "512 B", "48.2 KB", "1.4 GB".</summary>
    public static string Format(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:F0} {Units[unit]}" : $"{size:F1} {Units[unit]}";
    }
}
