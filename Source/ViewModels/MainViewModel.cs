using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly HttpService _httpService;
    private readonly RawSocketService _rawSocketService;
    private readonly PersistenceService _persistenceService;
    private readonly AiImportService _aiImportService;
    private readonly SavedRequestService _savedRequestService;
    private readonly RequestTabSessionService _tabSessionService;
    private SettingsModel _settings = new();
    private CancellationTokenSource? _aiImportCts;
    private Timer? _tabSessionSaveTimer;
    private bool _suppressTabSessionSave;

    public MainViewModel(HttpService httpService, RawSocketService rawSocketService,
        PersistenceService persistenceService, AiImportService aiImportService,
        SavedRequestService savedRequestService, RequestTabSessionService tabSessionService)
    {
        _httpService = httpService;
        _rawSocketService = rawSocketService;
        _persistenceService = persistenceService;
        _aiImportService = aiImportService;
        _savedRequestService = savedRequestService;
        _tabSessionService = tabSessionService;

        AiImportSelectedModel = _aiImportService.AvailableModels[0];

        // Session saves stay off until RestoreTabsFromDiskAsync has read tabs.json; a save
        // scheduled during startup (settings load resets the initial tab) would otherwise
        // overwrite the real session with this placeholder tab.
        _suppressTabSessionSave = true;
        AddBlankTab(select: true);
    }

    public ObservableCollection<RequestWorkspaceViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private RequestWorkspaceViewModel? _selectedTab;

    [ObservableProperty]
    private int _leftPanelTabIndex;

    [ObservableProperty]
    private int _editorFontSize = 13;

    [ObservableProperty]
    private bool _editorWordWrap = true;

    [ObservableProperty]
    private bool _isAiImportPanelOpen;

    [ObservableProperty]
    private string _aiImportInput = string.Empty;

    [ObservableProperty]
    private string _aiImportSelectedModel = string.Empty;

    [ObservableProperty]
    private bool _isAiImportProcessing;

    [ObservableProperty]
    private AiImportResult? _aiImportResult;

    [ObservableProperty]
    private string _aiImportError = string.Empty;

    [ObservableProperty]
    private string _aiImportPreviewJson = string.Empty;

    public string[] AiImportAvailableModels => _aiImportService.AvailableModels;

    public ObservableCollection<HistoryItem> HistoryItems { get; } = [];

    [ObservableProperty]
    private HistoryItem? _selectedHistoryItem;

    public ObservableCollection<SavedRequest> SavedRequests { get; } = [];

    [ObservableProperty]
    private SavedRequest? _selectedSavedRequest;

    public SettingsModel CurrentSettings => _settings;

    partial void OnSelectedTabChanged(RequestWorkspaceViewModel? value)
    {
        CloseSelectedTabCommand.NotifyCanExecuteChanged();
        DuplicateSelectedTabCommand.NotifyCanExecuteChanged();
        ScheduleTabSessionSave();
    }

    [RelayCommand]
    private void NewRequest()
    {
        AddBlankTab(select: true);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTab))]
    private void CloseSelectedTab()
    {
        CloseTab(SelectedTab);
    }

    [RelayCommand]
    private void CloseTab(RequestWorkspaceViewModel? tab)
    {
        if (tab is null)
            return;

        if (tab.IsDirty && !tab.IsBlank)
        {
            var result = MessageBox.Show(
                "Close this unsaved request tab?",
                "Close Tab",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;
        }

        tab.CancelCommand.Execute(null);
        var index = Tabs.IndexOf(tab);
        if (index >= 0)
            Tabs.RemoveAt(index);

        if (Tabs.Count == 0)
        {
            AddBlankTab(select: true);
        }
        else if (SelectedTab is null || !Tabs.Contains(SelectedTab))
        {
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        ScheduleTabSessionSave();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTab))]
    private void DuplicateSelectedTab()
    {
        if (SelectedTab is null)
            return;

        var duplicate = CreateWorkspace();
        duplicate.LoadRequestDraft(SelectedTab.BuildRequestModel(), "Duplicated tab", isDirty: true);
        Tabs.Insert(Tabs.IndexOf(SelectedTab) + 1, duplicate);
        SelectedTab = duplicate;
    }

    [RelayCommand]
    private void NextTab()
    {
        if (Tabs.Count <= 1)
            return;

        var index = SelectedTab is null ? -1 : Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index + 1 + Tabs.Count) % Tabs.Count];
    }

    [RelayCommand]
    private void PreviousTab()
    {
        if (Tabs.Count <= 1)
            return;

        var index = SelectedTab is null ? 0 : Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index - 1 + Tabs.Count) % Tabs.Count];
    }

    [RelayCommand]
    private async Task ExportRequestAsync()
    {
        if (SelectedTab is not null)
            await SelectedTab.ExportRequestAsync();
    }

    [RelayCommand]
    private async Task ImportRequestAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var request = await _persistenceService.LoadRequestAsync(dialog.FileName);
                if (request != null)
                {
                    var tab = GetSmartOpenTarget();
                    tab.LoadRequestDraft(request, $"Imported {Path.GetFileName(dialog.FileName)}", isDirty: true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not import request: {ex.Message}", "Import Request",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    [RelayCommand]
    private void CopyAsCurl()
    {
        SelectedTab?.CopyAsCurl();
    }

    [RelayCommand]
    private async Task SaveCurrentRequestAsync()
    {
        if (SelectedTab is not null)
            await SelectedTab.SaveCurrentRequestAsync();
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        HistoryItems.Clear();
        try
        {
            await _persistenceService.SaveHistoryAsync([], _settings.MaxHistoryItems, mergeWithDisk: false);
        }
        catch (Exception ex)
        {
            // History persistence is best-effort; the in-memory list is already cleared.
            ErrorLog.Write("Clear history", ex);
        }
    }

    [RelayCommand]
    private void LoadHistoryItem(HistoryItem? item)
    {
        if (item?.RequestSnapshot is null)
            return;

        // Always the exact request that was sent. If it came from a saved request that still
        // exists, the tab stays linked to it, so Save updates that entry — and the tab is
        // only marked dirty when the snapshot differs from what's saved.
        var request = _persistenceService.DeserializeRequest(item.RequestSnapshot, out var secretsReadable);
        if (request is null)
            return;

        var saved = item.SavedRequestId is { } id ? SavedRequests.FirstOrDefault(r => r.Id == id) : null;
        var status = $"Loaded {item.Method} {item.Url} from history";
        if (!secretsReadable)
            status += " - auth secrets couldn't be decrypted (saved by another Windows user or PC)";

        var tab = GetSmartOpenTarget();
        tab.LoadRequestDraft(request, status,
            isDirty: saved is null || !RequestsMatch(request, saved.Request), linkedSaved: saved);
    }

    [RelayCommand]
    private void LoadSavedRequest(SavedRequest? saved)
    {
        if (saved is null)
            return;

        SelectedSavedRequest = saved;

        // Already open in a tab: switch to it rather than opening a duplicate.
        var open = Tabs.FirstOrDefault(t => t.ActiveSavedRequestId == saved.Id);
        if (open != null)
        {
            SelectedTab = open;
            return;
        }

        var tab = GetSmartOpenTarget();
        tab.LoadSavedRequest(saved);
    }

    [RelayCommand]
    private async Task RenameSavedRequestAsync(SavedRequest? saved)
    {
        if (saved is null)
            return;

        var dialog = new Views.RenameDialog(saved.Name)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
        {
            saved.Name = dialog.NewName;
            saved.ModifiedAt = DateTime.Now;
            await TrySaveRequestFileAsync(saved);

            foreach (var tab in Tabs)
                tab.RenameSavedRequestLink(saved.Id, saved.Name);

            var idx = SavedRequests.IndexOf(saved);
            if (idx >= 0)
            {
                SavedRequests.RemoveAt(idx);
                SavedRequests.Insert(idx, saved);
            }
        }
    }

    [RelayCommand]
    private void DeleteSavedRequest(SavedRequest? saved)
    {
        if (saved is null)
            return;

        var confirm = MessageBox.Show($"Delete \"{saved.Name}\"? This can't be undone.", "Delete Request",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            _savedRequestService.Delete(saved.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not delete \"{saved.Name}\": {ex.Message}", "Delete Request",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SavedRequests.Remove(saved);

        foreach (var tab in Tabs)
            tab.ClearSavedRequestLink(saved.Id);
    }

    [RelayCommand]
    private async Task DuplicateSavedRequestAsync(SavedRequest? saved)
    {
        if (saved is null)
            return;

        var duplicate = new SavedRequest
        {
            Name = saved.Name + " (copy)",
            Request = saved.Request.Clone()
        };

        SavedRequests.Insert(0, duplicate);
        await TrySaveRequestFileAsync(duplicate);
    }

    private async Task TrySaveRequestFileAsync(SavedRequest saved)
    {
        try
        {
            await _savedRequestService.SaveAsync(saved);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save \"{saved.Name}\": {ex.Message}", "Save Request",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenAiImport()
    {
        AiImportInput = string.Empty;
        AiImportResult = null;
        AiImportError = string.Empty;
        AiImportPreviewJson = string.Empty;
        IsAiImportProcessing = false;
        IsAiImportPanelOpen = true;
    }

    [RelayCommand]
    private void EditAiImport()
    {
        AiImportResult = null;
        AiImportError = string.Empty;
        AiImportPreviewJson = string.Empty;
    }

    [RelayCommand]
    private void CloseAiImport()
    {
        _aiImportCts?.Cancel();
        IsAiImportPanelOpen = false;
        IsAiImportProcessing = false;
    }

    [RelayCommand]
    private async Task RunAiImportAsync()
    {
        if (string.IsNullOrWhiteSpace(AiImportInput))
        {
            AiImportError = "Please paste a curl command, API snippet, or request description.";
            return;
        }

        AiImportError = string.Empty;
        AiImportResult = null;
        AiImportPreviewJson = string.Empty;
        IsAiImportProcessing = true;
        _aiImportCts = new CancellationTokenSource();

        try
        {
            var result = await _aiImportService.ParseRequestAsync(
                AiImportInput, AiImportSelectedModel, _aiImportCts.Token);

            AiImportResult = result;
            AiImportPreviewJson = FormatRequestPreviewJson(result.Request);
        }
        catch (InvalidOperationException ex)
        {
            AiImportError = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            AiImportError = ex.Message;
        }
        catch (TimeoutException ex)
        {
            AiImportError = ex.Message;
        }
        catch (OperationCanceledException)
        {
            AiImportError = "Import cancelled.";
        }
        catch (Exception ex)
        {
            AiImportError = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            IsAiImportProcessing = false;
            _aiImportCts?.Dispose();
            _aiImportCts = null;
        }
    }

    [RelayCommand]
    private void ApplyAiImport()
    {
        if (AiImportResult?.Request is null)
            return;

        var tab = GetSmartOpenTarget();
        tab.LoadRequestDraft(AiImportResult.Request,
            $"Imported {AiImportResult.Request.Method} {AiImportResult.Request.Url}", isDirty: true);
        IsAiImportPanelOpen = false;
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var editCopy = _settings.Clone();
        var dialog = new PayloadPanda.Views.SettingsWindow(editCopy, _aiImportService.AvailableModels)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            var previousHistoryPath = _persistenceService.EffectiveHistoryFilePath;
            _settings = editCopy;
            ApplySettings();
            try
            {
                await _persistenceService.SaveSettingsAsync(_settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Settings applied but could not be saved to disk: {ex.Message}",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (!string.Equals(Path.GetFullPath(previousHistoryPath),
                    Path.GetFullPath(_persistenceService.EffectiveHistoryFilePath), StringComparison.OrdinalIgnoreCase))
            {
                await SwitchHistoryFileAsync();
            }
        }
    }

    public async Task LoadSettingsFromDiskAsync()
    {
        _settings = await _persistenceService.LoadSettingsAsync();
        ApplySettings();

        if (SelectedTab is { IsBlank: true, IsDirty: false } tab)
            tab.ResetToBlank();
    }

    public async Task LoadHistoryFromDiskAsync()
    {
        var items = await _persistenceService.LoadHistoryAsync();
        foreach (var item in items)
            HistoryItems.Add(item);
    }

    // The history file location changed. An existing file there is adopted as-is (never
    // overwritten); a new location starts from the current history, which is then merged
    // into it.
    private async Task SwitchHistoryFileAsync()
    {
        var path = _persistenceService.EffectiveHistoryFilePath;
        if (File.Exists(path))
        {
            HistoryItems.Clear();
            await LoadHistoryFromDiskAsync();
            TrimHistory();
        }
        else
        {
            await PersistHistoryAsync(HistoryItems.ToList());
        }
    }

    // False while the library is still loading at startup (tabs are restored before it).
    public bool IsSavedLibraryLoaded { get; private set; }

    public async Task LoadSavedRequestsFromDiskAsync()
    {
        try
        {
            var items = await _savedRequestService.LoadAllAsync();
            foreach (var item in items)
                SavedRequests.Add(item);
        }
        finally
        {
            IsSavedLibraryLoaded = true;
        }
    }

    public async Task RestoreTabsFromDiskAsync()
    {
        _suppressTabSessionSave = true;
        _tabSessionSaveTimer?.Dispose();
        _tabSessionSaveTimer = null;
        try
        {
            await RestoreTabsCoreAsync();
        }
        catch
        {
            // A bad session/autosave file must never block startup; the finally
            // block guarantees the window opens with at least one usable tab.
        }
        finally
        {
            if (Tabs.Count == 0)
                AddBlankTab(select: true);
            _suppressTabSessionSave = false;
        }

        ScheduleTabSessionSave();
    }

    private async Task RestoreTabsCoreAsync()
    {
        var session = await _tabSessionService.LoadAsync();
        Tabs.Clear();

        if (session?.Tabs.Count > 0)
        {
            // Links to saved requests are kept as stored; RelinkTabsToSavedRequests checks
            // them once the library has loaded, so restoring never waits on it.
            foreach (var draft in session.Tabs)
            {
                var tab = CreateWorkspace();
                tab.ApplyDraft(draft);
                Tabs.Add(tab);
            }

            SelectedTab = Tabs[Math.Clamp(session.SelectedIndex, 0, Tabs.Count - 1)];
        }
        else
        {
            var autosave = await _savedRequestService.LoadAutosaveAsync();
            if (autosave?.Request != null)
            {
                var tab = CreateWorkspace();
                tab.LoadRequestDraft(autosave.Request, "Restored from autosave", isDirty: true);
                if (autosave.Id != Guid.Empty)
                    tab.ActiveSavedRequestId = autosave.Id; // checked by RelinkTabsToSavedRequests

                Tabs.Add(tab);
                SelectedTab = tab;
                _savedRequestService.ClearAutosave();
            }
            else
            {
                AddBlankTab(select: true);
            }
        }
    }

    // Restored tabs remember which saved request they belong to. Once the library has
    // loaded, refresh their names (renamed since) or drop links to deleted entries.
    public void RelinkTabsToSavedRequests()
    {
        foreach (var tab in Tabs)
        {
            if (tab.ActiveSavedRequestId is not { } id)
                continue;

            var saved = SavedRequests.FirstOrDefault(r => r.Id == id);
            if (saved != null)
                tab.RenameSavedRequestLink(id, saved.Name);
            else
                tab.ClearSavedRequestLink(id);
        }
    }

    public async Task SaveTabsSessionNowAsync()
    {
        // Until the session has been restored, Tabs holds only a startup placeholder;
        // writing it would replace the user's real session.
        if (_suppressTabSessionSave)
            return;

        _tabSessionSaveTimer?.Dispose();
        _tabSessionSaveTimer = null;

        try
        {
            var selectedIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab));
            var session = new RequestTabSession
            {
                SelectedIndex = selectedIndex,
                Tabs = Tabs.Select(tab => tab.ToDraft()).ToList()
            };
            await _tabSessionService.SaveAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Tab drafts are best-effort; saved requests remain the durable source.
            ErrorLog.Write("Save tab session", ex);
        }
    }

    internal void ScheduleTabSessionSave()
    {
        if (_suppressTabSessionSave)
            return;

        _tabSessionSaveTimer?.Dispose();
        _tabSessionSaveTimer = new Timer(_ =>
        {
            Application.Current?.Dispatcher.BeginInvoke(SaveTabsSessionNowAsync);
        }, null, 1000, Timeout.Infinite);
    }

    internal void AddHistoryItem(HistoryItem item)
    {
        HistoryItems.Insert(0, item);
        TrimHistory();
        _ = PersistHistoryAsync(HistoryItems.ToList());
    }

    private async Task PersistHistoryAsync(List<HistoryItem> snapshot)
    {
        try
        {
            await _persistenceService.SaveHistoryAsync(snapshot, _settings.MaxHistoryItems);
        }
        catch (Exception ex)
        {
            // History persistence is best-effort and runs fire-and-forget after every
            // send — a failed write must never surface as an unobserved exception.
            ErrorLog.Write("Save history", ex);
        }
    }

    internal void MoveSavedRequestToTop(SavedRequest saved)
    {
        if (!SavedRequests.Contains(saved))
        {
            SavedRequests.Insert(0, saved);
            return;
        }

        var idx = SavedRequests.IndexOf(saved);
        if (idx > 0)
        {
            SavedRequests.RemoveAt(idx);
            SavedRequests.Insert(0, saved);
        }
    }

    private RequestWorkspaceViewModel AddBlankTab(bool select)
    {
        var tab = CreateWorkspace();
        Tabs.Add(tab);
        if (select)
            SelectedTab = tab;
        ScheduleTabSessionSave();
        return tab;
    }

    private RequestWorkspaceViewModel CreateWorkspace()
    {
        return new RequestWorkspaceViewModel(this, _httpService, _rawSocketService,
            _persistenceService, _savedRequestService);
    }

    private RequestWorkspaceViewModel GetSmartOpenTarget()
    {
        if (SelectedTab is { IsBlank: true } tab)
            return tab;

        return AddBlankTab(select: true);
    }

    private bool HasSelectedTab() => SelectedTab is not null;

    private void ApplySettings()
    {
        _persistenceService.SetHistoryFilePath(_settings.HistoryFilePath);
        _httpService.SslVerification = _settings.SslCertificateVerification;
        _rawSocketService.SslVerification = _settings.SslCertificateVerification;
        _aiImportService.Configure(_settings.OpenAiApiKey, _settings.AiEndpoint, _settings.AiTimeoutSeconds);
        EditorFontSize = _settings.EditorFontSize;
        EditorWordWrap = _settings.EditorWordWrap;

        foreach (var tab in Tabs)
            tab.ApplyEditorSettings(EditorFontSize, EditorWordWrap);

        if (_aiImportService.AvailableModels.Contains(_settings.AiDefaultModel))
            AiImportSelectedModel = _settings.AiDefaultModel;

        // A lowered limit applies right away, not only after the next send.
        TrimHistory();
    }

    private void TrimHistory()
    {
        if (_settings.MaxHistoryItems > 0 && HistoryItems.Count > _settings.MaxHistoryItems)
        {
            while (HistoryItems.Count > _settings.MaxHistoryItems)
                HistoryItems.RemoveAt(HistoryItems.Count - 1);
        }
    }

    private static bool RequestsMatch(RequestModel a, RequestModel b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    private static string FormatRequestPreviewJson(RequestModel request) =>
        JsonSerializer.Serialize(request, JsonDefaults.Display);
}
