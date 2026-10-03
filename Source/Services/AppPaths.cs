using System.IO;

namespace PayloadPanda.Services;

// Single source for the data folder (%AppData%/PayloadPanda). PAYLOADPANDA_DATA_DIR
// overrides it so tests and smoke runs never touch the real user data.
internal static class AppPaths
{
    public const string DataDirEnvironmentVariable = "PAYLOADPANDA_DATA_DIR";

    public static string DataFolder
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
            var folder = !string.IsNullOrWhiteSpace(overridePath)
                ? overridePath
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PayloadPanda");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}
