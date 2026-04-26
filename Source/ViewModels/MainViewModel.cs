using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly HttpService _httpService;
    private readonly PersistenceService _persistenceService;
    private readonly AiImportService _aiImportService;
    private readonly SavedRequestService _savedRequestService;
    private SettingsModel _settings = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _aiImportCts;

    // Autosave timer: fires once after 2s of inactivity, then must be reset
    private Timer? _autosaveTimer;
    private bool _suppressAutosave;

    public MainViewModel(HttpService httpService, PersistenceService persistenceService,
        AiImportService aiImportService, SavedRequestService savedRequestService)
    {
        _httpService = httpService;
        _persistenceService = persistenceService;
        _aiImportService = aiImportService;
        _savedRequestService = savedRequestService;

        AiImportSelectedModel = _aiImportService.AvailableModels[0];

        RequestHeaders.Add(new HeaderItem());
        QueryParams.Add(new QueryParamItem());

        // Subscribe to collection changes for autosave
        RequestHeaders.CollectionChanged += OnCollectionChangedForAutosave;
        QueryParams.CollectionChanged += OnCollectionChangedForAutosave;
    }

    // ==================== Request State ====================

    [ObservableProperty]
    private HttpMethodType _selectedMethod = HttpMethodType.GET;

    [ObservableProperty]
    private string _requestUrl = string.Empty;

    public ObservableCollection<HeaderItem> RequestHeaders { get; } = [];

    public ObservableCollection<QueryParamItem> QueryParams { get; } = [];

    [ObservableProperty]
    private BodyMode _selectedBodyMode = BodyMode.None;

    [ObservableProperty]
    private string _requestBody = string.Empty;

    [ObservableProperty]
    private AuthMode _selectedAuthMode = AuthMode.None;

    [ObservableProperty]
    private string _authToken = string.Empty;

    [ObservableProperty]
    private string _authUsername = string.Empty;

    [ObservableProperty]
    private string _authPassword = string.Empty;

    [ObservableProperty]
    private string _apiKeyHeader = "X-API-Key";

    [ObservableProperty]
    private string _apiKeyValue = string.Empty;

    // ==================== Response State ====================

    [ObservableProperty]
    private ResponseModel? _currentResponse;

    [ObservableProperty]
    private string _responseBody = string.Empty;

    [ObservableProperty]
    private string _rawResponseBody = string.Empty;

    [ObservableProperty]
    private ObservableCollection<KeyValuePair<string, string>> _responseHeaders = [];

    // ==================== UI State ====================

    [ObservableProperty]
    private int _selectedRequestTabIndex;

    [ObservableProperty]
    private int _selectedResponseTabIndex;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private int _leftPanelTabIndex;

    // ==================== Editor Settings ====================

    [ObservableProperty]
    private int _editorFontSize = 13;

    [ObservableProperty]
    private bool _editorWordWrap = true;

    // ==================== AI Import State ====================

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

    // ==================== History ====================

    public ObservableCollection<HistoryItem> HistoryItems { get; } = [];

    [ObservableProperty]
    private HistoryItem? _selectedHistoryItem;

    // ==================== Saved Requests ====================

    public ObservableCollection<SavedRequest> SavedRequests { get; } = [];

    [ObservableProperty]
    private SavedRequest? _selectedSavedRequest;

    [ObservableProperty]
    private Guid? _activeSavedRequestId;

    [ObservableProperty]
    private string _activeRequestName = string.Empty;

    // ==================== Enums for ComboBox binding ====================

    public HttpMethodType[] AvailableMethods { get; } = Enum.GetValues<HttpMethodType>();
    public BodyMode[] AvailableBodyModes { get; } = Enum.GetValues<BodyMode>();
    public AuthMode[] AvailableAuthModes { get; } = Enum.GetValues<AuthMode>();

    // ==================== Autosave Partial Methods ====================

    partial void OnSelectedMethodChanged(HttpMethodType value) => ResetAutosaveTimer();
    partial void OnRequestUrlChanged(string value) => ResetAutosaveTimer();
    partial void OnRequestBodyChanged(string value) => ResetAutosaveTimer();
    partial void OnSelectedBodyModeChanged(BodyMode value) => ResetAutosaveTimer();
    partial void OnSelectedAuthModeChanged(AuthMode value) => ResetAutosaveTimer();
    partial void OnAuthTokenChanged(string value) => ResetAutosaveTimer();
    partial void OnAuthUsernameChanged(string value) => ResetAutosaveTimer();
    partial void OnAuthPasswordChanged(string value) => ResetAutosaveTimer();
    partial void OnApiKeyHeaderChanged(string value) => ResetAutosaveTimer();
    partial void OnApiKeyValueChanged(string value) => ResetAutosaveTimer();

    private void OnCollectionChangedForAutosave(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Subscribe to PropertyChanged on new items
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is INotifyPropertyChanged npc)
                    npc.PropertyChanged += OnItemPropertyChangedForAutosave;
            }
        }
        // Unsubscribe from removed items
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is INotifyPropertyChanged npc)
                    npc.PropertyChanged -= OnItemPropertyChangedForAutosave;
            }
        }
        ResetAutosaveTimer();
    }

    private void OnItemPropertyChangedForAutosave(object? sender, PropertyChangedEventArgs e)
    {
        ResetAutosaveTimer();
    }

    private void ResetAutosaveTimer()
    {
        if (_suppressAutosave) return;
        _autosaveTimer?.Dispose();
        _autosaveTimer = new Timer(_ =>
        {
            Application.Current?.Dispatcher.BeginInvoke(PerformAutosave);
        }, null, 2000, Timeout.Infinite);
    }

    private async void PerformAutosave()
    {
        try
        {
            var request = BuildRequestModel();
            var saved = new SavedRequest
            {
                Id = ActiveSavedRequestId ?? Guid.Empty,
                Name = ActiveRequestName,
                Request = request
            };
            await _savedRequestService.SaveAutosaveAsync(saved);
        }
        catch
        {
            // Autosave is best-effort
        }
    }

    // ==================== Commands ====================

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(RequestUrl))
        {
            StatusText = "Please enter a URL";
            return;
        }

        var url = RequestUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
            RequestUrl = url;
        }

        IsLoading = true;
        StatusText = "Sending...";
        _cts = new CancellationTokenSource();

        try
        {
            var requestModel = BuildRequestModel();
            var response = await _httpService.SendAsync(requestModel, _cts.Token);

            CurrentResponse = response;
            RawResponseBody = response.Body;
            ResponseBody = TryFormatJson(response.Body);
            ResponseHeaders = new ObservableCollection<KeyValuePair<string, string>>(response.Headers);

            StatusText = $"{response.StatusCode} {response.ReasonPhrase} — {response.Duration.TotalMilliseconds:F0}ms";

            // Auto-save request
            SavedRequest savedReq;
            if (ActiveSavedRequestId.HasValue)
            {
                // Update existing saved request
                savedReq = SavedRequests.FirstOrDefault(r => r.Id == ActiveSavedRequestId.Value)
                           ?? new SavedRequest { Name = $"{SelectedMethod} {RequestUrl}" };
                savedReq.Request = requestModel;
                savedReq.ModifiedAt = DateTime.Now;
            }
            else
            {
                // Create new saved request
                savedReq = new SavedRequest
                {
                    Name = $"{SelectedMethod} {RequestUrl}",
                    Request = requestModel
                };
                ActiveSavedRequestId = savedReq.Id;
                ActiveRequestName = savedReq.Name;
                SavedRequests.Insert(0, savedReq);
            }
            await _savedRequestService.SaveAsync(savedReq);

            // Ensure it's at top of list if already exists
            if (SavedRequests.Contains(savedReq) && SavedRequests.IndexOf(savedReq) != 0)
            {
                SavedRequests.Remove(savedReq);
                SavedRequests.Insert(0, savedReq);
            }

            // Add to history with saved request link
            var historyItem = new HistoryItem
            {
                Timestamp = DateTime.Now,
                Method = SelectedMethod,
                Url = RequestUrl,
                StatusCode = response.StatusCode,
                Duration = response.Duration,
                RequestSnapshot = _persistenceService.SerializeRequest(requestModel),
                SavedRequestId = savedReq.Id,
                SavedRequestName = savedReq.Name
            };
            HistoryItems.Insert(0, historyItem);
            TrimHistory();
            _ = _persistenceService.SaveHistoryAsync(HistoryItems.ToList());
        }
        catch (HttpRequestException ex)
        {
            StatusText = $"Error: {ex.Message}";
            CurrentResponse = null;
            ResponseBody = string.Empty;
            RawResponseBody = string.Empty;
            ResponseHeaders = [];
        }
        catch (OperationCanceledException)
        {
            StatusText = _cts?.IsCancellationRequested == true ? "Request cancelled" : "Request timed out";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    // Export (was Save) — file dialog export
    [RelayCommand]
    private async Task ExportRequestAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "request"
        };

        if (dialog.ShowDialog() == true)
        {
            var request = BuildRequestModel();
            await _persistenceService.SaveRequestAsync(request, dialog.FileName);
            StatusText = $"Exported to {System.IO.Path.GetFileName(dialog.FileName)}";
        }
    }

    // Import (was Load) — file dialog import
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
            var request = await _persistenceService.LoadRequestAsync(dialog.FileName);
            if (request != null)
            {
                ActiveSavedRequestId = null;
                ActiveRequestName = string.Empty;
                PopulateFromRequest(request);
                StatusText = $"Imported {System.IO.Path.GetFileName(dialog.FileName)}";
            }
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        HistoryItems.Clear();
        await _persistenceService.SaveHistoryAsync([]);
        StatusText = "History cleared";
    }

    [RelayCommand]
    private void CopyResponseBody()
    {
        if (!string.IsNullOrEmpty(ResponseBody))
        {
            Clipboard.SetText(ResponseBody);
            StatusText = "Response copied to clipboard";
        }
    }

    [RelayCommand]
    private void CopyAsCurl()
    {
        var curl = GenerateCurlCommand();
        if (!string.IsNullOrEmpty(curl))
        {
            Clipboard.SetText(curl);
            StatusText = "Curl command copied to clipboard";
        }
    }

    [RelayCommand]
    private void LoadHistoryItem(HistoryItem? item)
    {
        if (item is null) return;

        // Try to load from saved request if linked and still exists
        if (item.SavedRequestId.HasValue)
        {
            var saved = SavedRequests.FirstOrDefault(r => r.Id == item.SavedRequestId.Value);
            if (saved != null)
            {
                LoadSavedRequest(saved);
                return;
            }
        }

        // Fall back to snapshot
        if (item.RequestSnapshot is null) return;
        var request = _persistenceService.DeserializeRequest(item.RequestSnapshot);
        if (request != null)
        {
            ActiveSavedRequestId = null;
            ActiveRequestName = string.Empty;
            PopulateFromRequest(request);
            StatusText = $"Loaded {item.Method} {item.Url}";
        }
    }

    // ==================== Saved Request Commands ====================

    [RelayCommand]
    private void NewRequest()
    {
        _suppressAutosave = true;
        ActiveSavedRequestId = null;
        ActiveRequestName = string.Empty;

        SelectedMethod = HttpMethodType.GET;
        RequestUrl = string.Empty;
        RequestHeaders.Clear();
        RequestHeaders.Add(new HeaderItem());
        QueryParams.Clear();
        QueryParams.Add(new QueryParamItem());
        SelectedBodyMode = BodyMode.None;
        RequestBody = string.Empty;
        SelectedAuthMode = AuthMode.None;
        AuthToken = string.Empty;
        AuthUsername = string.Empty;
        AuthPassword = string.Empty;
        ApiKeyHeader = "X-API-Key";
        ApiKeyValue = string.Empty;

        CurrentResponse = null;
        ResponseBody = string.Empty;
        RawResponseBody = string.Empty;
        ResponseHeaders = [];

        StatusText = "New request";
        _suppressAutosave = false;
        _savedRequestService.ClearAutosave();
    }

    [RelayCommand]
    private async Task SaveCurrentRequestAsync()
    {
        var requestModel = BuildRequestModel();

        if (ActiveSavedRequestId.HasValue)
        {
            // Update existing
            var existing = SavedRequests.FirstOrDefault(r => r.Id == ActiveSavedRequestId.Value);
            if (existing != null)
            {
                existing.Request = requestModel;
                existing.ModifiedAt = DateTime.Now;
                await _savedRequestService.SaveAsync(existing);
                StatusText = $"Saved \"{existing.Name}\"";
                // Refresh collection to update UI
                var idx = SavedRequests.IndexOf(existing);
                if (idx >= 0)
                {
                    SavedRequests.RemoveAt(idx);
                    SavedRequests.Insert(0, existing);
                }
                return;
            }
        }

        // Create new
        var name = string.IsNullOrWhiteSpace(RequestUrl) ? "New Request" : $"{SelectedMethod} {RequestUrl}";
        var saved = new SavedRequest
        {
            Name = name,
            Request = requestModel
        };
        ActiveSavedRequestId = saved.Id;
        ActiveRequestName = saved.Name;
        SavedRequests.Insert(0, saved);
        await _savedRequestService.SaveAsync(saved);
        StatusText = $"Saved \"{saved.Name}\"";
    }

    [RelayCommand]
    private void LoadSavedRequest(SavedRequest? saved)
    {
        if (saved is null) return;
        _suppressAutosave = true;

        ActiveSavedRequestId = saved.Id;
        ActiveRequestName = saved.Name;
        PopulateFromRequest(saved.Request);
        SelectedSavedRequest = saved;
        StatusText = $"Loaded \"{saved.Name}\"";

        _suppressAutosave = false;
        ResetAutosaveTimer();
    }

    [RelayCommand]
    private async Task RenameSavedRequestAsync(SavedRequest? saved)
    {
        if (saved is null) return;

        var dialog = new Views.RenameDialog(saved.Name)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
        {
            saved.Name = dialog.NewName;
            saved.ModifiedAt = DateTime.Now;
            await _savedRequestService.SaveAsync(saved);

            if (ActiveSavedRequestId == saved.Id)
                ActiveRequestName = saved.Name;

            // Refresh the item in the collection to update UI
            var idx = SavedRequests.IndexOf(saved);
            if (idx >= 0)
            {
                SavedRequests.RemoveAt(idx);
                SavedRequests.Insert(idx, saved);
            }
            StatusText = $"Renamed to \"{saved.Name}\"";
        }
    }

    [RelayCommand]
    private void DeleteSavedRequest(SavedRequest? saved)
    {
        if (saved is null) return;

        _savedRequestService.Delete(saved.Id);
        SavedRequests.Remove(saved);

        if (ActiveSavedRequestId == saved.Id)
        {
            ActiveSavedRequestId = null;
            ActiveRequestName = string.Empty;
        }

        StatusText = $"Deleted \"{saved.Name}\"";
    }

    [RelayCommand]
    private async Task DuplicateSavedRequestAsync(SavedRequest? saved)
    {
        if (saved is null) return;

        var requestJson = JsonSerializer.Serialize(saved.Request);
        var clonedRequest = JsonSerializer.Deserialize<RequestModel>(requestJson) ?? new RequestModel();

        var duplicate = new SavedRequest
        {
            Name = saved.Name + " (copy)",
            Request = clonedRequest
        };

        SavedRequests.Insert(0, duplicate);
        await _savedRequestService.SaveAsync(duplicate);
        StatusText = $"Duplicated \"{saved.Name}\"";
    }

    [RelayCommand]
    private void AddHeaderRow()
    {
        RequestHeaders.Add(new HeaderItem());
    }

    [RelayCommand]
    private void RemoveHeaderRow(HeaderItem? item)
    {
        if (item != null && RequestHeaders.Count > 1)
        {
            RequestHeaders.Remove(item);
        }
    }

    [RelayCommand]
    private void AddParamRow()
    {
        QueryParams.Add(new QueryParamItem());
    }

    [RelayCommand]
    private void RemoveParamRow(QueryParamItem? item)
    {
        if (item != null && QueryParams.Count > 1)
        {
            QueryParams.Remove(item);
        }
    }

    // ==================== AI Import Commands ====================

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
        if (AiImportResult?.Request is null) return;

        PopulateFromRequest(AiImportResult.Request);
        StatusText = $"Imported {AiImportResult.Request.Method} {AiImportResult.Request.Url}";
        IsAiImportPanelOpen = false;
    }

    private static string FormatRequestPreviewJson(RequestModel request)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(request, options);
    }

    // ==================== Settings Commands ====================

    [RelayCommand]
    private void OpenSettings()
    {
        var editCopy = _settings.Clone();
        var dialog = new PayloadPanda.Views.SettingsWindow(editCopy, _aiImportService.AvailableModels)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            _settings = editCopy;
            ApplySettings();
            _ = _persistenceService.SaveSettingsAsync(_settings);
            StatusText = "Settings saved";
        }
    }

    // ==================== Public Methods ====================

    public SettingsModel CurrentSettings => _settings;

    public async Task LoadSettingsFromDiskAsync()
    {
        _settings = await _persistenceService.LoadSettingsAsync();
        ApplySettings();
    }

    public async Task LoadHistoryFromDiskAsync()
    {
        var items = await _persistenceService.LoadHistoryAsync();
        foreach (var item in items)
        {
            HistoryItems.Add(item);
        }
    }

    public async Task LoadSavedRequestsFromDiskAsync()
    {
        var items = await _savedRequestService.LoadAllAsync();
        foreach (var item in items)
        {
            SavedRequests.Add(item);
        }
    }

    public async Task RestoreAutosaveAsync()
    {
        var autosave = await _savedRequestService.LoadAutosaveAsync();
        if (autosave?.Request is null) return;

        _suppressAutosave = true;

        if (autosave.Id != Guid.Empty)
        {
            ActiveSavedRequestId = autosave.Id;
            ActiveRequestName = autosave.Name;
        }

        PopulateFromRequest(autosave.Request);
        StatusText = "Restored from autosave";
        _suppressAutosave = false;
    }

    // ==================== Private Helpers ====================

    private void ApplySettings()
    {
        _persistenceService.SetHistoryFilePath(_settings.HistoryFilePath);
        _httpService.SslVerification = _settings.SslCertificateVerification;
        _aiImportService.Configure(_settings.OpenAiApiKey, _settings.AiEndpoint, _settings.AiTimeoutSeconds);
        EditorFontSize = _settings.EditorFontSize;
        EditorWordWrap = _settings.EditorWordWrap;

        if (_aiImportService.AvailableModels.Contains(_settings.AiDefaultModel))
            AiImportSelectedModel = _settings.AiDefaultModel;
    }

    private void TrimHistory()
    {
        if (_settings.MaxHistoryItems > 0 && HistoryItems.Count > _settings.MaxHistoryItems)
        {
            while (HistoryItems.Count > _settings.MaxHistoryItems)
                HistoryItems.RemoveAt(HistoryItems.Count - 1);
        }
    }

    private RequestModel BuildRequestModel()
    {
        return new RequestModel
        {
            Method = SelectedMethod,
            Url = RequestUrl,
            Headers = RequestHeaders
                .Where(h => !string.IsNullOrWhiteSpace(h.Key))
                .Select(h => new HeaderItemData { Key = h.Key, Value = h.Value, IsEnabled = h.IsEnabled })
                .ToList(),
            QueryParams = QueryParams
                .Where(p => !string.IsNullOrWhiteSpace(p.Key))
                .Select(p => new QueryParamData { Key = p.Key, Value = p.Value, IsEnabled = p.IsEnabled })
                .ToList(),
            BodyMode = SelectedBodyMode,
            BodyText = RequestBody,
            AuthMode = SelectedAuthMode,
            AuthToken = AuthToken,
            AuthUsername = AuthUsername,
            AuthPassword = AuthPassword,
            ApiKeyHeader = ApiKeyHeader,
            ApiKeyValue = ApiKeyValue,
            TimeoutSeconds = _settings.DefaultTimeoutSeconds,
            FollowRedirects = _settings.DefaultFollowRedirects
        };
    }

    private void PopulateFromRequest(RequestModel request)
    {
        SelectedMethod = request.Method;
        RequestUrl = request.Url;

        RequestHeaders.Clear();
        foreach (var h in request.Headers)
            RequestHeaders.Add(new HeaderItem { Key = h.Key, Value = h.Value, IsEnabled = h.IsEnabled });
        if (RequestHeaders.Count == 0)
            RequestHeaders.Add(new HeaderItem());

        QueryParams.Clear();
        foreach (var p in request.QueryParams)
            QueryParams.Add(new QueryParamItem { Key = p.Key, Value = p.Value, IsEnabled = p.IsEnabled });
        if (QueryParams.Count == 0)
            QueryParams.Add(new QueryParamItem());

        SelectedBodyMode = request.BodyMode;
        RequestBody = request.BodyText;
        SelectedAuthMode = request.AuthMode;
        AuthToken = request.AuthToken;
        AuthUsername = request.AuthUsername;
        AuthPassword = request.AuthPassword;
        ApiKeyHeader = request.ApiKeyHeader;
        ApiKeyValue = request.ApiKeyValue;
    }

    private string GenerateCurlCommand()
    {
        var sb = new StringBuilder("curl");

        if (SelectedMethod != HttpMethodType.GET)
            sb.Append($" -X {SelectedMethod}");

        sb.Append($" '{RequestUrl}'");

        // Headers
        foreach (var h in RequestHeaders.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
            sb.Append($" \\\n  -H '{h.Key}: {h.Value}'");

        // Auth
        switch (SelectedAuthMode)
        {
            case AuthMode.Bearer:
                sb.Append($" \\\n  -H 'Authorization: Bearer {AuthToken}'");
                break;
            case AuthMode.Basic:
                sb.Append($" \\\n  -u '{AuthUsername}:{AuthPassword}'");
                break;
            case AuthMode.ApiKey:
                var key = string.IsNullOrWhiteSpace(ApiKeyHeader) ? "X-API-Key" : ApiKeyHeader;
                sb.Append($" \\\n  -H '{key}: {ApiKeyValue}'");
                break;
        }

        // Body
        if (SelectedBodyMode != BodyMode.None && !string.IsNullOrWhiteSpace(RequestBody))
        {
            var contentType = SelectedBodyMode switch
            {
                BodyMode.Json => "application/json",
                BodyMode.Xml => "application/xml",
                BodyMode.FormUrlEncoded => "application/x-www-form-urlencoded",
                _ => "text/plain"
            };
            sb.Append($" \\\n  -H 'Content-Type: {contentType}'");
            sb.Append($" \\\n  -d '{RequestBody.Replace("'", "'\\''")}'");
        }

        return sb.ToString();
    }

    private static string TryFormatJson(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        try
        {
            using var doc = JsonDocument.Parse(input);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return input;
        }
    }
}
