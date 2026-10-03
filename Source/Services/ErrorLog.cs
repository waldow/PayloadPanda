using System.Diagnostics;
using System.IO;

namespace PayloadPanda.Services;

// Best-effort record of unexpected exceptions (errors.log in the data folder), so
// failures swallowed to keep the app alive still leave a trace to debug from.
internal static class ErrorLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object s_lock = new();

    public static void Write(string context, Exception exception)
    {
        Debug.WriteLine($"[{context}] {exception}");
        try
        {
            lock (s_lock)
            {
                var path = Path.Combine(AppPaths.DataFolder, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);

                File.AppendAllText(path, $"{DateTime.Now:O} [{context}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never become a second failure.
        }
    }
}
