using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.ViewModels;

public partial class RequestWorkspaceViewModel : ObservableObject
{
    private const int ConnectionTabIndex = 3;

    private readonly MainViewModel _owner;
    private readonly HttpService _httpService;
    private readonly RawSocketService _rawSocketService;
    private readonly PersistenceService _persistenceService;
    private readonly SavedRequestService _savedRequestService;
    private CancellationTokenSource? _cts;
    private bool _suppressChangeNotifications;

    public RequestWorkspaceViewModel(MainViewModel owner, HttpService httpService,
        RawSocketService rawSocketService, PersistenceService persistenceService,
        SavedRequestService savedRequestService)
    {
        _owner = owner;
        _httpService = httpService;
        _rawSocketService = rawSocketService;
        _persistenceService = persistenceService;
        _savedRequestService = savedRequestService;

        EditorFontSize = owner.EditorFontSize;
        EditorWordWrap = owner.EditorWordWrap;
        RequestTimeoutSeconds = owner.CurrentSettings.DefaultTimeoutSeconds;
        RequestFollowRedirects = owner.CurrentSettings.DefaultFollowRedirects;

        _suppressChangeNotifications = true;
        RequestHeaders.CollectionChanged += OnCollectionChangedForWorkspace;
        QueryParams.CollectionChanged += OnCollectionChangedForWorkspace;
        RequestHeaders.Add(new HeaderItem());
        QueryParams.Add(new QueryParamItem());
        IsDirty = false;
        _suppressChangeNotifications = false;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public HttpMethodType[] AvailableMethods { get; } = Enum.GetValues<HttpMethodType>();
    public BodyMode[] AvailableBodyModes { get; } = Enum.GetValues<BodyMode>();
    public AuthMode[] AvailableAuthModes { get; } = Enum.GetValues<AuthMode>();

    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ActiveRequestName))
                return ActiveRequestName;

            if (!string.IsNullOrWhiteSpace(RequestUrl))
            {
                if (Uri.TryCreate(RequestUrl, UriKind.Absolute, out var uri))
                {
                    var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"
                        ? string.Empty
                        : uri.AbsolutePath;
                    return $"{SelectedMethod} {uri.Host}{path}";
                }

                return $"{SelectedMethod} {RequestUrl}";
            }

            return "Untitled";
        }
    }

    public bool IsBlank =>
        !ActiveSavedRequestId.HasValue &&
        string.IsNullOrWhiteSpace(ActiveRequestName) &&
        string.IsNullOrWhiteSpace(RequestUrl) &&
        SelectedMethod == HttpMethodType.GET &&
        SelectedBodyMode == BodyMode.None &&
        string.IsNullOrWhiteSpace(RequestBody) &&
        SelectedAuthMode == AuthMode.None &&
        string.IsNullOrWhiteSpace(AuthToken) &&
        string.IsNullOrWhiteSpace(AuthUsername) &&
        string.IsNullOrWhiteSpace(AuthPassword) &&
        string.IsNullOrWhiteSpace(ApiKeyValue) &&
        SelectedRequestMode == RequestMode.Http &&
        CurrentResponse is null &&
        !HasDiagnostics &&
        RequestHeaders.All(h => string.IsNullOrWhiteSpace(h.Key) && string.IsNullOrWhiteSpace(h.Value)) &&
        QueryParams.All(p => string.IsNullOrWhiteSpace(p.Key) && string.IsNullOrWhiteSpace(p.Value));

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

    [ObservableProperty]
    private int _requestTimeoutSeconds = 30;

    [ObservableProperty]
    private bool _requestFollowRedirects = true;

    [ObservableProperty]
    private RequestMode _selectedRequestMode = RequestMode.Http;

    [ObservableProperty]
    private ResponseModel? _currentResponse;

    [ObservableProperty]
    private string _responseBody = string.Empty;

    [ObservableProperty]
    private string _rawResponseBody = string.Empty;

    [ObservableProperty]
    private ObservableCollection<KeyValuePair<string, string>> _responseHeaders = [];

    [ObservableProperty]
    private ConnectionDiagnostics? _connectionDiagnostics;

    [ObservableProperty]
    private bool _hasDiagnostics;

    [ObservableProperty]
    private ObservableCollection<TimingPhaseRow> _timingPhases = [];

    [ObservableProperty]
    private int _selectedRequestTabIndex;

    [ObservableProperty]
    private int _selectedResponseTabIndex;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private Guid? _activeSavedRequestId;

    [ObservableProperty]
    private string _activeRequestName = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private int _editorFontSize = 13;

    [ObservableProperty]
    private bool _editorWordWrap = true;

    partial void OnSelectedMethodChanged(HttpMethodType value) => MarkRequestChanged(refreshTitle: true);
    partial void OnRequestUrlChanged(string value) => MarkRequestChanged(refreshTitle: true);
    partial void OnSelectedBodyModeChanged(BodyMode value) => MarkRequestChanged();
    partial void OnRequestBodyChanged(string value) => MarkRequestChanged();
    partial void OnSelectedAuthModeChanged(AuthMode value) => MarkRequestChanged();
    partial void OnAuthTokenChanged(string value) => MarkRequestChanged();
    partial void OnAuthUsernameChanged(string value) => MarkRequestChanged();
    partial void OnAuthPasswordChanged(string value) => MarkRequestChanged();
    partial void OnApiKeyHeaderChanged(string value) => MarkRequestChanged();
    partial void OnApiKeyValueChanged(string value) => MarkRequestChanged();
    partial void OnRequestTimeoutSecondsChanged(int value) => MarkRequestChanged();
    partial void OnRequestFollowRedirectsChanged(bool value) => MarkRequestChanged();
    partial void OnSelectedRequestModeChanged(RequestMode value) => MarkRequestChanged();
    partial void OnSelectedRequestTabIndexChanged(int value) => MarkUiChanged();
    partial void OnSelectedResponseTabIndexChanged(int value) => MarkUiChanged();
    partial void OnActiveRequestNameChanged(string value) => OnPropertyChanged(nameof(Title));
    partial void OnIsDirtyChanged(bool value) => _owner.ScheduleTabSessionSave();
    partial void OnCurrentResponseChanged(ResponseModel? value) => DownloadResponseCommand.NotifyCanExecuteChanged();

    public void ApplyEditorSettings(int fontSize, bool wordWrap)
    {
        EditorFontSize = fontSize;
        EditorWordWrap = wordWrap;
    }

    public RequestTabDraft ToDraft() => new()
    {
        Id = Id,
        SavedRequestId = ActiveSavedRequestId,
        ActiveRequestName = ActiveRequestName,
        RequestMode = SelectedRequestMode,
        SelectedRequestTabIndex = SelectedRequestTabIndex,
        SelectedResponseTabIndex = SelectedResponseTabIndex,
        IsDirty = IsDirty,
        Request = BuildRequestModel()
    };

    public void ApplyDraft(RequestTabDraft draft)
    {
        _suppressChangeNotifications = true;
        Id = draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id;
        ActiveSavedRequestId = draft.SavedRequestId;
        ActiveRequestName = draft.ActiveRequestName;
        SelectedRequestMode = draft.RequestMode;
        SelectedRequestTabIndex = Math.Max(0, draft.SelectedRequestTabIndex);
        SelectedResponseTabIndex = Math.Max(0, draft.SelectedResponseTabIndex);
        PopulateFromRequest(draft.Request);
        ClearResponse();
        IsDirty = draft.IsDirty;
        StatusText = "Restored tab";
        _suppressChangeNotifications = false;
        RefreshTitleAndSession();
    }

    public void LoadSavedRequest(SavedRequest saved)
    {
        _suppressChangeNotifications = true;
        ActiveSavedRequestId = saved.Id;
        ActiveRequestName = saved.Name;
        SelectedRequestMode = RequestMode.Http;
        PopulateFromRequest(saved.Request);
        ClearResponse();
        StatusText = $"Loaded \"{saved.Name}\"";
        IsDirty = false;
        _suppressChangeNotifications = false;
        RefreshTitleAndSession();
    }

    public void LoadRequestDraft(RequestModel request, string statusText, bool isDirty)
    {
        _suppressChangeNotifications = true;
        ActiveSavedRequestId = null;
        ActiveRequestName = string.Empty;
        SelectedRequestMode = RequestMode.Http;
        PopulateFromRequest(request);
        ClearResponse();
        StatusText = statusText;
        IsDirty = isDirty;
        _suppressChangeNotifications = false;
        RefreshTitleAndSession();
    }

    public void ResetToBlank()
    {
        _suppressChangeNotifications = true;
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
        RequestTimeoutSeconds = _owner.CurrentSettings.DefaultTimeoutSeconds;
        RequestFollowRedirects = _owner.CurrentSettings.DefaultFollowRedirects;
        SelectedRequestMode = RequestMode.Http;
        SelectedRequestTabIndex = 0;
        SelectedResponseTabIndex = 0;
        ClearResponse();
        StatusText = "New request";
        IsDirty = false;
        _suppressChangeNotifications = false;
        RefreshTitleAndSession();
    }

    public void ClearSavedRequestLink(Guid savedRequestId)
    {
        if (ActiveSavedRequestId != savedRequestId)
            return;

        ActiveSavedRequestId = null;
        ActiveRequestName = string.Empty;
        IsDirty = !IsBlank;
        RefreshTitleAndSession();
    }

    public void RenameSavedRequestLink(Guid savedRequestId, string name)
    {
        if (ActiveSavedRequestId != savedRequestId)
            return;

        ActiveRequestName = name;
        RefreshTitleAndSession();
    }

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

        if (!IsRequestTimeoutValid())
        {
            StatusText = "Timeout must be between 1 and 300 seconds";
            return;
        }

        ClearResponse();
        IsLoading = true;
        StatusText = "Sending...";
        _cts = new CancellationTokenSource();

        try
        {
            var requestModel = BuildRequestModel();
            var response = SelectedRequestMode == RequestMode.RawSocket
                ? await _rawSocketService.SendAsync(requestModel, _cts.Token)
                : await _httpService.SendAsync(requestModel, _cts.Token);

            CurrentResponse = response;
            RawResponseBody = response.Body;
            ResponseBody = TryFormatJson(response.Body);
            ResponseHeaders = new ObservableCollection<KeyValuePair<string, string>>(response.Headers);
            SetDiagnostics(response.Diagnostics);
            if (response.Diagnostics != null)
                SelectedResponseTabIndex = ConnectionTabIndex;

            StatusText = $"{response.StatusCode} {response.ReasonPhrase} - {response.Duration.TotalMilliseconds:F0}ms";

            var savedReq = await SaveRequestForSendAsync(requestModel);
            _owner.AddHistoryItem(new HistoryItem
            {
                Timestamp = DateTime.Now,
                Method = SelectedMethod,
                Url = RequestUrl,
                StatusCode = response.StatusCode,
                Duration = response.Duration,
                RequestSnapshot = _persistenceService.SerializeRequest(requestModel),
                SavedRequestId = savedReq.Id,
                SavedRequestName = savedReq.Name
            });

            IsDirty = false;
        }
        catch (HttpRequestException ex)
        {
            StatusText = $"Error: {ex.Message}";
            ClearResponse();
        }
        catch (OperationCanceledException)
        {
            StatusText = _cts?.IsCancellationRequested == true ? "Request cancelled" : "Request timed out";
            ClearResponse();
        }
        catch (RawSocketException ex)
        {
            ClearResponse();
            SetDiagnostics(ex.Diagnostics);
            SelectedResponseTabIndex = ConnectionTabIndex;
            StatusText = $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            ClearResponse();
        }
        finally
        {
            IsLoading = false;
            _cts?.Dispose();
            _cts = null;
            _owner.ScheduleTabSessionSave();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    [RelayCommand]
    public async Task ExportRequestAsync()
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
            StatusText = $"Exported to {Path.GetFileName(dialog.FileName)}";
        }
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

    [RelayCommand(CanExecute = nameof(CanDownloadResponse))]
    private async Task DownloadResponseAsync()
    {
        if (CurrentResponse is not { } response)
        {
            StatusText = "No response to download";
            return;
        }

        var suggestedFileName = GetResponseDownloadFileName(response);
        var extension = Path.GetExtension(suggestedFileName);
        var dialog = new SaveFileDialog
        {
            Filter = BuildDownloadFilter(extension),
            DefaultExt = string.IsNullOrEmpty(extension) ? ".bin" : extension,
            FileName = suggestedFileName
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, response.BodyBytes);
            StatusText = $"Downloaded {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusText = $"Download failed: {ex.Message}";
        }
    }

    private bool CanDownloadResponse() => CurrentResponse is not null;

    [RelayCommand]
    public void CopyAsCurl()
    {
        var curl = GenerateCurlCommand();
        if (!string.IsNullOrEmpty(curl))
        {
            Clipboard.SetText(curl);
            StatusText = "Curl command copied to clipboard";
        }
    }

    [RelayCommand]
    public async Task SaveCurrentRequestAsync()
    {
        var requestModel = BuildRequestModel();

        if (ActiveSavedRequestId.HasValue)
        {
            var existing = _owner.SavedRequests.FirstOrDefault(r => r.Id == ActiveSavedRequestId.Value);
            if (existing != null)
            {
                existing.Request = requestModel;
                existing.ModifiedAt = DateTime.Now;
                await _savedRequestService.SaveAsync(existing);
                _owner.MoveSavedRequestToTop(existing);
                ActiveRequestName = existing.Name;
                IsDirty = false;
                StatusText = $"Saved \"{existing.Name}\"";
                return;
            }
        }

        var name = string.IsNullOrWhiteSpace(RequestUrl) ? "New Request" : $"{SelectedMethod} {RequestUrl}";
        var saved = new SavedRequest
        {
            Name = name,
            Request = requestModel
        };

        ActiveSavedRequestId = saved.Id;
        ActiveRequestName = saved.Name;
        _owner.SavedRequests.Insert(0, saved);
        await _savedRequestService.SaveAsync(saved);
        IsDirty = false;
        StatusText = $"Saved \"{saved.Name}\"";
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
            RequestHeaders.Remove(item);
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
            QueryParams.Remove(item);
    }

    public RequestModel BuildRequestModel()
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
            TimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 1, 300),
            FollowRedirects = RequestFollowRedirects
        };
    }

    private async Task<SavedRequest> SaveRequestForSendAsync(RequestModel requestModel)
    {
        SavedRequest savedReq;
        if (ActiveSavedRequestId.HasValue)
        {
            var existing = _owner.SavedRequests.FirstOrDefault(r => r.Id == ActiveSavedRequestId.Value);
            if (existing != null)
            {
                savedReq = existing;
                savedReq.Request = requestModel;
                savedReq.ModifiedAt = DateTime.Now;
            }
            else
            {
                savedReq = new SavedRequest
                {
                    Name = $"{SelectedMethod} {RequestUrl}",
                    Request = requestModel
                };
                ActiveSavedRequestId = savedReq.Id;
                ActiveRequestName = savedReq.Name;
                _owner.SavedRequests.Insert(0, savedReq);
            }
        }
        else
        {
            savedReq = new SavedRequest
            {
                Name = $"{SelectedMethod} {RequestUrl}",
                Request = requestModel
            };
            ActiveSavedRequestId = savedReq.Id;
            ActiveRequestName = savedReq.Name;
            _owner.SavedRequests.Insert(0, savedReq);
        }

        await _savedRequestService.SaveAsync(savedReq);
        _owner.MoveSavedRequestToTop(savedReq);
        return savedReq;
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
        ApiKeyHeader = string.IsNullOrWhiteSpace(request.ApiKeyHeader) ? "X-API-Key" : request.ApiKeyHeader;
        ApiKeyValue = request.ApiKeyValue;
        RequestTimeoutSeconds = request.TimeoutSeconds is >= 1 and <= 300
            ? request.TimeoutSeconds
            : _owner.CurrentSettings.DefaultTimeoutSeconds;
        RequestFollowRedirects = request.FollowRedirects;
    }

    private bool IsRequestTimeoutValid() => RequestTimeoutSeconds is >= 1 and <= 300;

    private void ClearResponse()
    {
        CurrentResponse = null;
        ResponseBody = string.Empty;
        RawResponseBody = string.Empty;
        ResponseHeaders = [];
        ConnectionDiagnostics = null;
        HasDiagnostics = false;
        TimingPhases = [];
    }

    private void SetDiagnostics(ConnectionDiagnostics? diagnostics)
    {
        ConnectionDiagnostics = diagnostics;
        HasDiagnostics = diagnostics != null;
        TimingPhases = diagnostics != null ? BuildTimingPhases(diagnostics) : [];
    }

    private static ObservableCollection<TimingPhaseRow> BuildTimingPhases(ConnectionDiagnostics diagnostics)
    {
        var t = diagnostics.Timings;
        var rows = new List<TimingPhaseRow>
        {
            new() { Label = "DNS", Milliseconds = t.DnsMs },
            new() { Label = "TCP", Milliseconds = t.TcpConnectMs }
        };
        if (diagnostics.IsSecure)
            rows.Add(new TimingPhaseRow { Label = "TLS", Milliseconds = t.TlsHandshakeMs });
        rows.Add(new TimingPhaseRow { Label = "TTFB", Milliseconds = t.TimeToFirstByteMs });

        var scaleMax = Math.Max(rows.Max(r => r.Milliseconds), 1);
        foreach (var row in rows)
            row.ScaleMax = scaleMax;

        return new ObservableCollection<TimingPhaseRow>(rows);
    }

    private string GenerateCurlCommand()
    {
        var sb = new StringBuilder("curl");

        if (SelectedMethod != HttpMethodType.GET)
            sb.Append($" -X {SelectedMethod}");

        if (RequestFollowRedirects)
            sb.Append(" -L");

        if (IsRequestTimeoutValid())
            sb.Append($" --max-time {RequestTimeoutSeconds}");

        sb.Append($" '{RequestUrl}'");

        foreach (var h in RequestHeaders.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
            sb.Append($" \\\n  -H '{h.Key}: {h.Value}'");

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

    private string GetResponseDownloadFileName(ResponseModel response)
    {
        var extension = GetDefaultExtension(response);
        var fileName = GetContentDispositionFileName(response);

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = GetRequestUrlFileName();

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"response-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";

        fileName = SanitizeFileName(fileName);

        if (!Path.HasExtension(fileName))
            fileName += extension;

        return fileName;
    }

    private static string GetContentDispositionFileName(ResponseModel response)
    {
        if (!TryGetHeader(response, "Content-Disposition", out var contentDisposition))
            return string.Empty;

        if (ContentDispositionHeaderValue.TryParse(contentDisposition, out var parsed))
        {
            var fileName = FirstNonEmpty(parsed.FileNameStar, parsed.FileName);
            if (!string.IsNullOrWhiteSpace(fileName))
                return TrimFileNameQuotes(fileName);
        }

        return ExtractContentDispositionFileName(contentDisposition);
    }

    private string GetRequestUrlFileName()
    {
        if (!Uri.TryCreate(RequestUrl, UriKind.Absolute, out var uri))
            return string.Empty;

        var fileName = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(fileName) ? string.Empty : Uri.UnescapeDataString(fileName);
    }

    private static string ExtractContentDispositionFileName(string contentDisposition)
    {
        string? fileName = null;
        string? fileNameStar = null;

        foreach (var segment in contentDisposition.Split(';'))
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex < 0)
                continue;

            var key = segment[..equalsIndex].Trim();
            var value = segment[(equalsIndex + 1)..].Trim();

            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
                fileNameStar = DecodeRfc5987FileName(value);
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase))
                fileName = TrimFileNameQuotes(value);
        }

        return FirstNonEmpty(fileNameStar, fileName);
    }

    private static string DecodeRfc5987FileName(string value)
    {
        value = TrimFileNameQuotes(value);
        var parts = value.Split('\'', 3);
        var encodedFileName = parts.Length == 3 ? parts[2] : value;
        return Uri.UnescapeDataString(encodedFileName);
    }

    private static string GetDefaultExtension(ResponseModel response)
    {
        var contentType = response.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) && TryGetHeader(response, "Content-Type", out var headerContentType))
            contentType = headerContentType;

        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mediaType switch
        {
            "application/json" => ".json",
            "application/pdf" => ".pdf",
            "application/xml" => ".xml",
            "application/zip" => ".zip",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "image/gif" => ".gif",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "text/csv" => ".csv",
            "text/html" => ".html",
            "text/plain" => ".txt",
            "text/xml" => ".xml",
            _ => ".bin"
        };
    }

    private static string BuildDownloadFilter(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return "All Files (*.*)|*.*";

        extension = extension.StartsWith('.') ? extension : "." + extension;
        var label = extension[1..].ToUpperInvariant();
        return $"{label} Files (*{extension})|*{extension}|All Files (*.*)|*.*";
    }

    private static bool TryGetHeader(ResponseModel response, string headerName, out string value)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                value = header.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string SanitizeFileName(string fileName)
    {
        var sanitized = TrimFileNameQuotes(fileName).Trim();

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(invalidChar, '_');

        sanitized = sanitized.Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "response" : sanitized;
    }

    private static string TrimFileNameQuotes(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];

        return value.Replace("\\\"", "\"");
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
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

    private void MarkRequestChanged(bool refreshTitle = false)
    {
        if (_suppressChangeNotifications)
            return;

        IsDirty = true;
        if (refreshTitle)
            OnPropertyChanged(nameof(Title));
        _owner.ScheduleTabSessionSave();
    }

    private void MarkUiChanged()
    {
        if (_suppressChangeNotifications)
            return;

        _owner.ScheduleTabSessionSave();
    }

    private void RefreshTitleAndSession()
    {
        OnPropertyChanged(nameof(Title));
        _owner.ScheduleTabSessionSave();
    }

    private void OnCollectionChangedForWorkspace(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is INotifyPropertyChanged npc)
                    npc.PropertyChanged += OnItemPropertyChangedForWorkspace;
            }
        }

        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is INotifyPropertyChanged npc)
                    npc.PropertyChanged -= OnItemPropertyChangedForWorkspace;
            }
        }

        MarkRequestChanged();
    }

    private void OnItemPropertyChangedForWorkspace(object? sender, PropertyChangedEventArgs e)
    {
        MarkRequestChanged();
    }
}
