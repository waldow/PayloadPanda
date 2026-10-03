using System.Windows;
using PayloadPanda.Services;
using PayloadPanda.ViewModels;

namespace PayloadPanda;

public partial class App : Application
{
    public static HttpService HttpService { get; } = new();
    public static RawSocketService RawSocketService { get; } = new();
    public static PersistenceService PersistenceService { get; } = new();
    public static AiImportService AiImportService { get; } = new();
    public static SavedRequestService SavedRequestService { get; } = new();
    public static RequestTabSessionService RequestTabSessionService { get; } = new();
    public static MainViewModel MainViewModel { get; } = new(HttpService, RawSocketService, PersistenceService, AiImportService, SavedRequestService, RequestTabSessionService);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-resort handlers: surface the error instead of tearing the app down,
        // and keep a record in errors.log.
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write("Unhandled UI exception", args.Exception);
            MessageBox.Show($"Unexpected error: {args.Exception.Message}",
                "PayloadPanda", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Each load is isolated so, e.g., a bad history file doesn't also hide the saved requests.
        // Tabs are restored right after settings so the window shows the real session at once;
        // the saved-request library (potentially thousands of files) loads after that.
        var failures = new List<string>();
        await TryLoadAsync("settings", MainViewModel.LoadSettingsFromDiskAsync, failures);
        await MainViewModel.RestoreTabsFromDiskAsync();
        await TryLoadAsync("history", MainViewModel.LoadHistoryFromDiskAsync, failures);
        if (await TryLoadAsync("saved requests", MainViewModel.LoadSavedRequestsFromDiskAsync, failures))
            MainViewModel.RelinkTabsToSavedRequests(); // only against a complete library
        if (failures.Count > 0)
        {
            MessageBox.Show($"Failed to load saved data:\n\n{string.Join("\n", failures)}\n\nStarting with defaults for those.",
                "PayloadPanda", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static async Task<bool> TryLoadAsync(string what, Func<Task> load, List<string> failures)
    {
        try
        {
            await load();
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Load {what}", ex);
            failures.Add($"{what}: {ex.Message}");
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MainViewModel.SaveTabsSessionNowAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
