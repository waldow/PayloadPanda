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

        // Last-resort handlers: surface the error instead of tearing the app down.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Unexpected error: {args.Exception.Message}",
                "PayloadPanda", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        try
        {
            await MainViewModel.LoadSettingsFromDiskAsync();
            await MainViewModel.LoadHistoryFromDiskAsync();
            await MainViewModel.LoadSavedRequestsFromDiskAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load saved data: {ex.Message}\n\nStarting with defaults.",
                "PayloadPanda", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await MainViewModel.RestoreTabsFromDiskAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MainViewModel.SaveTabsSessionNowAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
