using System.Windows;
using PayloadPanda.Services;
using PayloadPanda.ViewModels;

namespace PayloadPanda;

public partial class App : Application
{
    public static HttpService HttpService { get; } = new();
    public static PersistenceService PersistenceService { get; } = new();
    public static AiImportService AiImportService { get; } = new();
    public static SavedRequestService SavedRequestService { get; } = new();
    public static MainViewModel MainViewModel { get; } = new(HttpService, PersistenceService, AiImportService, SavedRequestService);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await MainViewModel.LoadSettingsFromDiskAsync();
        await MainViewModel.LoadHistoryFromDiskAsync();
        await MainViewModel.LoadSavedRequestsFromDiskAsync();
        await MainViewModel.RestoreAutosaveAsync();
    }
}
