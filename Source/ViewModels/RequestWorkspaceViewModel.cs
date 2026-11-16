using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PayloadPanda.Models;
using PayloadPanda.Services;
using PayloadPanda.Services.Scripting;

namespace PayloadPanda.ViewModels;

public partial class RequestWorkspaceViewModel : ObservableObject
{
    private const int ConnectionTabIndex = 3;
    private const int TestsTabIndex = 4;

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
        FormFields.CollectionChanged += OnCollectionChangedForWorkspace;
        RequestHeaders.Add(new HeaderItem());
        QueryParams.Add(new QueryParamItem());
        FormFields.Add(new FormFieldItem());
        IsDirty = false;
        _suppressChangeNotifications = false;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public HttpMethodType[] AvailableMethods { get; } = Enum.GetValues<HttpMethodType>();
    public FormFieldKind[] FormFieldKinds { get; } = Enum.GetValues<FormFieldKind>();
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
        !ActiveCollectionId.HasValue &&
        string.IsNullOrWhiteSpace(ActiveRequestName) &&
        string.IsNullOrWhiteSpace(PreRequestScript) &&
        string.IsNullOrWhiteSpace(TestScript) &&
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
        !CorsEnabled &&
        string.IsNullOrWhiteSpace(CorsOrigin) &&
        string.IsNullOrWhiteSpace(CorsRequestMethod) &&
        string.IsNullOrWhiteSpace(CorsRequestHeaders) &&
        !CorsIncludeCredentials &&
        CurrentResponse is null &&
        !HasDiagnostics &&
        RequestHeaders.All(h => string.IsNullOrWhiteSpace(h.Key) && string.IsNullOrWhiteSpace(h.Value)) &&
        QueryParams.All(p => string.IsNullOrWhiteSpace(p.Key) && string.IsNullOrWhiteSpace(p.Value)) &&
        FormFields.All(f => f.IsBlank) &&
        string.IsNullOrWhiteSpace(BinaryFilePath);

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

    // URL-encoded and form-data rows (one list, so switching between the two keeps them).
    public ObservableCollection<FormFieldItem> FormFields { get; } = [];

    // The file sent as the whole body in Binary mode.
    [ObservableProperty]
    private string _binaryFilePath = string.Empty;

    [ObservableProperty]
    private AuthMode _selectedAuthMode = AuthMode.None;

    [ObservableProperty]
    private string _authToken = string.Empty;

    [ObservableProperty]
    private string _authUsername = string.Empty;

    [ObservableProperty]
    private string _authPassword = string.Empty;

    [ObservableProperty]
    private string _apiKeyHeader = RequestComposer.DefaultApiKeyHeader;

    [ObservableProperty]
    private string _apiKeyValue = string.Empty;

    [ObservableProperty]
    private int _requestTimeoutSeconds = 30;

    [ObservableProperty]
    private bool _requestFollowRedirects = true;

    [ObservableProperty]
    private bool _corsEnabled;

    [ObservableProperty]
    private string _corsOrigin = string.Empty;

    [ObservableProperty]
    private string _corsRequestMethod = string.Empty;

    [ObservableProperty]
    private string _corsRequestHeaders = string.Empty;

    [ObservableProperty]
    private bool _corsIncludeCredentials;

    [ObservableProperty]
    private CorsAnalysisResult? _corsVerdict;

    [ObservableProperty]
    private bool _hasCorsVerdict;

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

    // The collection whose variables and scripts apply to this tab, and the request in
    // it that Save writes back to. The item can be gone (deleted) while the collection stays.
    [ObservableProperty]
    private Guid? _activeCollectionId;

    [ObservableProperty]
    private Guid? _activeCollectionItemId;

    [ObservableProperty]
    private string _preRequestScript = string.Empty;

    [ObservableProperty]
    private string _testScript = string.Empty;

    // What the scripts of the last send produced (shown on the response's Tests tab).
    [ObservableProperty]
    private ObservableCollection<ScriptTestResult> _testResults = [];

    [ObservableProperty]
    private string _scriptConsoleText = string.Empty;

    [ObservableProperty]
    private string _scriptError = string.Empty;

    [ObservableProperty]
    private bool _hasScriptOutput;

    [ObservableProperty]
    private string _testsTabHeader = "Tests";

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private int _editorFontSize = 13;

    [ObservableProperty]
    private bool _editorWordWrap = true;

    partial void OnSelectedMethodChanged(HttpMethodType value) => MarkRequestChanged(refreshTitle: true);
    partial void OnRequestUrlChanged(string value) => MarkRequestChanged(refreshTitle: true);
    partial void OnSelectedBodyModeChanged(BodyMode value) => MarkRequestChanged();
    partial void OnBinaryFilePathChanged(string value)
    {
        RefreshBinaryFileInfo();
        MarkRequestChanged();
    }
    partial void OnRequestBodyChanged(string value) => MarkRequestChanged();
    partial void OnSelectedAuthModeChanged(AuthMode value) => MarkRequestChanged();
    partial void OnAuthTokenChanged(string value) => MarkRequestChanged();
    partial void OnAuthUsernameChanged(string value) => MarkRequestChanged();
    partial void OnAuthPasswordChanged(string value) => MarkRequestChanged();
    partial void OnApiKeyHeaderChanged(string value) => MarkRequestChanged();
    partial void OnApiKeyValueChanged(string value) => MarkRequestChanged();
    partial void OnRequestTimeoutSecondsChanged(int value) => MarkRequestChanged();
    partial void OnRequestFollowRedirectsChanged(bool value) => MarkRequestChanged();
    partial void OnCorsEnabledChanged(bool value) => MarkRequestChanged();
    partial void OnCorsOriginChanged(string value) => MarkRequestChanged();
    partial void OnCorsRequestMethodChanged(string value) => MarkRequestChanged();
    partial void OnCorsRequestHeadersChanged(string value) => MarkRequestChanged();
    partial void OnCorsIncludeCredentialsChanged(bool value) => MarkRequestChanged();
    // The transport is kept with the tab, not with the request, so switching it isn't an edit.
    partial void OnSelectedRequestModeChanged(RequestMode value) => MarkUiChanged();
    partial void OnPreRequestScriptChanged(string value) => MarkRequestChanged();
    partial void OnTestScriptChanged(string value) => MarkRequestChanged();
    partial void OnActiveCollectionIdChanged(Guid? value) => OnPropertyChanged(nameof(VariableScopeText));
    partial void OnSelectedRequestTabIndexChanged(int value) => MarkUiChanged();
    partial void OnSelectedResponseTabIndexChanged(int value) => MarkUiChanged();
    partial void OnActiveRequestNameChanged(string value) => OnPropertyChanged(nameof(Title));
    partial void OnIsDirtyChanged(bool value) => _owner.ScheduleTabSessionSave();
    partial void OnCurrentResponseChanged(ResponseModel? value) => DownloadResponseCommand.NotifyCanExecuteChanged();

    // The Connection tab is hidden without diagnostics; leaving it selected would show its
    // empty content with no tab header highlighted.
    partial void OnHasDiagnosticsChanged(bool value)
    {
        if (!value && SelectedResponseTabIndex == ConnectionTabIndex)
            SelectedResponseTabIndex = 0;
    }

    // Same for the Tests tab, which only exists while there is script output.
    partial void OnHasScriptOutputChanged(bool value)
    {
        if (!value && SelectedResponseTabIndex == TestsTabIndex)
            SelectedResponseTabIndex = 0;
    }

    /// <summary>Where {{variables}} on this tab come from, for the Scripts tab.</summary>
    public string VariableScopeText
    {
        get
        {
            var sources = new List<string>();
            if (_owner.Environments.Active is { } environment)
                sources.Add($"the environment \"{environment.Name}\"");
            if (_owner.Collections.Find(ActiveCollectionId) is { } collection)
                sources.Add($"the collection \"{collection.Name}\"");
            sources.Add("globals");
            return sources.Count == 1
                ? "{{variables}} come from globals only. Pick an environment in the title bar, or open a request from a collection, for more."
                : $"{{{{variables}}}} come from {string.Join(", then ", sources)}. The first that has a value wins.";
        }
    }

    // Whoever stores each variable layer, so what a script sets can be written back.
    private sealed record VariableOwners(CollectionModel? Collection, EnvironmentModel? Environment, EnvironmentModel Globals)
    {
        public VariableScopes ToScopes() => new()
        {
            Globals = Globals.EnabledVariables(),
            Collection = Collection?.EnabledVariables() ?? new Dictionary<string, string>(),
            Environment = Environment?.EnabledVariables() ?? new Dictionary<string, string>(),
            EnvironmentName = Environment?.Name
        };
    }

    private VariableOwners CurrentVariableOwners() =>
        new(_owner.Collections.Find(ActiveCollectionId), _owner.Environments.Active, _owner.Environments.Globals);

    public void ApplyEditorSettings(int fontSize, bool wordWrap)
    {
        EditorFontSize = fontSize;
        EditorWordWrap = wordWrap;
    }

    public RequestTabDraft ToDraft() => new()
    {
        Id = Id,
        SavedRequestId = ActiveSavedRequestId,
        CollectionId = ActiveCollectionId,
        CollectionItemId = ActiveCollectionItemId,
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
        ActiveCollectionId = draft.CollectionId;
        ActiveCollectionItemId = draft.CollectionItemId;
        ActiveRequestName = draft.ActiveRequestName ?? string.Empty;
        SelectedRequestMode = draft.RequestMode;
        SelectedRequestTabIndex = Math.Max(0, draft.SelectedRequestTabIndex);
        // Responses aren't persisted, so a restored tab has no diagnostics to show.
        SelectedResponseTabIndex = draft.SelectedResponseTabIndex is ConnectionTabIndex or TestsTabIndex
            ? 0
            : Math.Max(0, draft.SelectedResponseTabIndex);
        PopulateFromRequest(draft.Request);
        ClearResponse();
        IsDirty = draft.IsDirty;
        StatusText = draft.SecretsUnreadable ? $"Restored tab - {RequestSecrets.UnreadableNote}" : "Restored tab";
        _suppressChangeNotifications = false;
        RefreshTitleAndSession();
    }

    public void LoadSavedRequest(SavedRequest saved)
    {
        _suppressChangeNotifications = true;
        ActiveSavedRequestId = saved.Id;
        ActiveCollectionId = null;
        ActiveCollectionItemId = null;
        ActiveRequestName = saved.Name;
        SelectedRequestMode = RequestMode.Http;
        PopulateFromRequest(saved.Request);
        ClearResponse();
        StatusText = saved.SecretsUnreadable
            ? $"Loaded \"{saved.Name}\" - {RequestSecrets.UnreadableNote}"
            : $"Loaded \"{saved.Name}\"";
        IsDirty = false;
        _suppressChangeNotifications = false;
        RefreshTitleAndSession();
    }

    /// <param name="linkedSaved">
    /// Saved request this draft belongs to (e.g. a history snapshot of it), so Save updates
    /// that entry instead of creating a new one.
    /// </param>
    /// <param name="collectionId">Collection whose variables and scripts apply to the draft.</param>
    /// <param name="collectionItem">The request in that collection the draft belongs to, if it still exists.</param>
    public void LoadRequestDraft(RequestModel request, string statusText, bool isDirty, SavedRequest? linkedSaved = null,
        Guid? collectionId = null, CollectionItem? collectionItem = null)
    {
        _suppressChangeNotifications = true;
        ActiveSavedRequestId = linkedSaved?.Id;
        ActiveCollectionId = collectionId;
        ActiveCollectionItemId = collectionItem?.Id;
        ActiveRequestName = linkedSaved?.Name ?? collectionItem?.Name ?? string.Empty;
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
        ActiveCollectionId = null;
        ActiveCollectionItemId = null;
        ActiveRequestName = string.Empty;
        PreRequestScript = string.Empty;
        TestScript = string.Empty;
        SelectedMethod = HttpMethodType.GET;
        RequestUrl = string.Empty;
        RequestHeaders.Clear();
        RequestHeaders.Add(new HeaderItem());
        QueryParams.Clear();
        QueryParams.Add(new QueryParamItem());
        FormFields.Clear();
        FormFields.Add(new FormFieldItem());
        BinaryFilePath = string.Empty;
        SelectedBodyMode = BodyMode.None;
        RequestBody = string.Empty;
        SelectedAuthMode = AuthMode.None;
        AuthToken = string.Empty;
        AuthUsername = string.Empty;
        AuthPassword = string.Empty;
        ApiKeyHeader = RequestComposer.DefaultApiKeyHeader;
        ApiKeyValue = string.Empty;
        RequestTimeoutSeconds = _owner.CurrentSettings.DefaultTimeoutSeconds;
        RequestFollowRedirects = _owner.CurrentSettings.DefaultFollowRedirects;
        CorsEnabled = false;
        CorsOrigin = string.Empty;
        CorsRequestMethod = string.Empty;
        CorsRequestHeaders = string.Empty;
        CorsIncludeCredentials = false;
        SelectedRequestMode = RequestMode.Http;
        SelectedRequestTabIndex = 0;
        SelectedResponseTabIndex = 0;
        ClearResponse();
        StatusText = "New request";
        IsDirty = false;
        _suppressChangeNotifications = false;
        RefreshTitleAndSession();
    }

    public void LoadCollectionItem(CollectionModel collection, CollectionItem item)
    {
        _suppressChangeNotifications = true;
        ActiveSavedRequestId = null;
        ActiveCollectionId = collection.Id;
        ActiveCollectionItemId = item.Id;
        ActiveRequestName = item.Name;
        SelectedRequestMode = RequestMode.Http;
        PopulateFromRequest(item.Request ?? new RequestModel());
        ClearResponse();
        StatusText = collection.SecretsUnreadable
            ? $"Loaded \"{item.Name}\" - {RequestSecrets.UnreadableNote}"
            : $"Loaded \"{item.Name}\"";
        IsDirty = false;
        _suppressChangeNotifications = false;
        RefreshTitleAndSession();
    }

    /// <summary>The tab's request was just saved into a collection: it now belongs to that item.</summary>
    public void LinkToCollectionItem(CollectionModel collection, CollectionItem item)
    {
        ActiveSavedRequestId = null;
        ActiveCollectionId = collection.Id;
        ActiveCollectionItemId = item.Id;
        ActiveRequestName = item.Name;
        IsDirty = false;
        RefreshTitleAndSession();
    }

    /// <param name="itemIds">The deleted requests, or null when the whole collection is gone.</param>
    public void ClearCollectionLink(Guid collectionId, IReadOnlySet<Guid>? itemIds)
    {
        if (ActiveCollectionId != collectionId)
            return;

        if (itemIds is null)
        {
            ActiveCollectionId = null;
        }
        else if (ActiveCollectionItemId is not { } itemId || !itemIds.Contains(itemId))
        {
            return;
        }

        if (ActiveCollectionItemId.HasValue)
        {
            ActiveCollectionItemId = null;
            ActiveRequestName = string.Empty;
            IsDirty = !IsBlank;
        }
        RefreshTitleAndSession();
    }

    public void RenameCollectionItemLink(Guid itemId, string name)
    {
        if (ActiveCollectionItemId != itemId)
            return;

        ActiveRequestName = name;
        RefreshTitleAndSession();
    }

    /// <summary>The linked collection's variables or name changed.</summary>
    public void RefreshVariableScope() => OnPropertyChanged(nameof(VariableScopeText));

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
        if (!TryPrepareUrl() || !ValidateTimeout())
            return;

        await RunRequestAsync(isPreflight: false);
    }

    [RelayCommand]
    private async Task SendPreflightAsync()
    {
        if (!CorsEnabled)
        {
            StatusText = "Enable CORS testing first";
            return;
        }

        if (!TryPrepareUrl() || !ValidateTimeout())
            return;

        // Preflight is a browser HTTP concept; it always goes through HttpService, never the raw socket.
        await RunRequestAsync(isPreflight: true);
    }

    // A request with its variables substituted, ready for a transport.
    private sealed record PreparedRequest(
        RequestModel Model, string CorsMethod, string CorsRequestedHeaders, IReadOnlyList<string> Unresolved);

    private sealed record ScriptChain(List<string> PreRequest, List<string> Test);

    /// <exception cref="FormatException">The URL (after substitution) or a header is invalid.</exception>
    private PreparedRequest PrepareSend(IReadOnlyDictionary<string, string> variables,
        IReadOnlyList<KeyValuePair<string, string>>? scriptedHeaders)
    {
        var (resolved, unresolved) = VariableResolver.ResolveRequest(BuildSendModel(scriptedHeaders), variables);
        resolved.Url = RequireValidUrl(resolved.Url, unresolved);
        return new PreparedRequest(resolved, SelectedMethod.ToString(), BuildCorsRequestedHeaders(resolved), unresolved);
    }

    /// <exception cref="FormatException">The URL (after substitution) or a header is invalid.</exception>
    private PreparedRequest PreparePreflight(IReadOnlyDictionary<string, string> variables)
    {
        var method = string.IsNullOrWhiteSpace(CorsRequestMethod)
            ? SelectedMethod.ToString()
            : CorsRequestMethod.Trim();

        var (baseModel, unresolved) = VariableResolver.ResolveRequest(BuildRequestModel(), variables);
        baseModel.Url = RequireValidUrl(baseModel.Url, unresolved);
        var requestedHeaders = BuildCorsRequestedHeaders(baseModel, method);
        return new PreparedRequest(BuildPreflightModel(baseModel, method, requestedHeaders), method, requestedHeaders, unresolved);
    }

    private static string RequireValidUrl(string url, IReadOnlyList<string> unresolved)
    {
        var normalized = NormalizeUrl(url);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
            return normalized;

        throw new FormatException(unresolved.Count > 0
            ? $"Invalid URL - no value for {PlaceholderNames(unresolved)}"
            : "Invalid URL");
    }

    private static string PlaceholderNames(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => "{{" + n + "}}"));

    private static List<KeyValuePair<string, string>> EnabledHeaders(RequestModel model) =>
        model.Headers
            .Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key))
            .Select(h => new KeyValuePair<string, string>(h.Key, h.Value))
            .ToList();

    // Scripts run outermost first: the collection's, each enclosing folder's, then the request's own.
    private ScriptChain BuildScriptChain(CollectionModel? collection)
    {
        var preRequest = new List<string>();
        var test = new List<string>();
        if (collection is not null)
        {
            preRequest.Add(collection.PreRequestScript);
            test.Add(collection.TestScript);
            if (ActiveCollectionItemId is { } itemId)
            {
                foreach (var folder in collection.FindPath(itemId).Where(i => i.IsFolder))
                {
                    preRequest.Add(folder.PreRequestScript);
                    test.Add(folder.TestScript);
                }
            }
        }
        preRequest.Add(PreRequestScript);
        test.Add(TestScript);

        preRequest.RemoveAll(string.IsNullOrWhiteSpace);
        test.RemoveAll(string.IsNullOrWhiteSpace);
        return new ScriptChain(preRequest, test);
    }

    // Writes what a script set or unset back to whoever stores that layer. Local values
    // (pm.variables.set) belong to the send and are never stored.
    private void ApplyVariableChanges(VariableOwners owners, VariableScopes before, ScriptResult result)
    {
        var after = result.Scopes;

        if (Differs(before.Collection, after.Collection))
        {
            if (owners.Collection is null)
            {
                result.Console.Add(new ScriptConsoleLine("warn",
                    "Collection variables set by the script weren't kept: this request isn't in a collection."));
            }
            else
            {
                Apply(owners.Collection.Variables, before.Collection, after.Collection);
                _owner.Collections.SaveInBackground(owners.Collection);
            }
        }

        if (Differs(before.Environment, after.Environment))
        {
            if (owners.Environment is null)
            {
                result.Console.Add(new ScriptConsoleLine("warn",
                    "No environment is selected, so values set with pm.environment were kept for this send only."));
            }
            else
            {
                Apply(owners.Environment.Variables, before.Environment, after.Environment);
                _owner.Environments.SaveInBackground(owners.Environment);
            }
        }

        if (Differs(before.Globals, after.Globals))
        {
            Apply(owners.Globals.Variables, before.Globals, after.Globals);
            _owner.Environments.SaveInBackground(owners.Globals);
        }

        static bool Differs(IReadOnlyDictionary<string, string> old, IReadOnlyDictionary<string, string> current) =>
            old.Count != current.Count || current.Any(v => !old.TryGetValue(v.Key, out var value) || value != v.Value);

        static void Apply(List<CollectionVariable> stored, IReadOnlyDictionary<string, string> old, IReadOnlyDictionary<string, string> current)
        {
            foreach (var (key, value) in current.Where(v => !old.TryGetValue(v.Key, out var previous) || previous != v.Value))
            {
                var (variable, added) = stored.SetValue(key, value);
                if (added && PostmanCollectionConverter.LooksSecret(key))
                    variable.IsSecret = true;
            }
            foreach (var key in old.Keys.Where(k => !current.ContainsKey(k)))
                stored.RemoveKey(key);
        }
    }

    private void ShowScriptOutput(ScriptResult? preRequest, ScriptResult? test)
    {
        var results = new[] { preRequest, test }.Where(r => r is not null).Select(r => r!).ToList();
        var tests = results.SelectMany(r => r.Tests).ToList();
        var console = results.SelectMany(r => r.Console)
            .Select(line => line.Level == "log" ? line.Text : $"[{line.Level}] {line.Text}");

        TestResults = new ObservableCollection<ScriptTestResult>(tests);
        ScriptConsoleText = string.Join(Environment.NewLine, console);
        ScriptError = preRequest is { Succeeded: false }
            ? $"Pre-request script: {preRequest.Error}"
            : test is { Succeeded: false } ? $"Test script: {test.Error}" : string.Empty;
        TestsTabHeader = tests.Count == 0 ? "Tests" : $"Tests ({tests.Count(t => t.Passed)}/{tests.Count})";
        HasScriptOutput = tests.Count > 0 || ScriptConsoleText.Length > 0 || ScriptError.Length > 0;
    }

    // Sending never touches the saved-request library or a collection's requests: the
    // request is recorded in history and stays a draft (IsDirty) until the user saves it.
    // (Variables a script sets are the exception: they are written to the collection.)
    private async Task RunRequestAsync(bool isPreflight)
    {
        // Send and Preflight are separate commands; without this guard they could
        // run concurrently and clobber each other's cancellation token.
        if (IsLoading)
        {
            StatusText = "A request is already running";
            return;
        }

        // Snapshot before the first await: the plain request (placeholders as typed, no
        // injected Origin header), so history entries stay clean and hold no variable values.
        var historyModel = isPreflight ? null : BuildRequestModel();
        var requestName = Title;
        var owners = CurrentVariableOwners();
        var scopes = owners.ToScopes();
        var scripts = isPreflight || !_owner.CurrentSettings.RunScripts ? null : BuildScriptChain(owners.Collection);

        ClearResponse();
        IsLoading = true;
        StatusText = isPreflight ? "Sending preflight..." : "Sending...";
        var cts = new CancellationTokenSource();
        _cts = cts;
        ScriptResult? preResult = null;

        try
        {
            IReadOnlyList<KeyValuePair<string, string>>? scriptedHeaders = null;
            if (scripts is { PreRequest.Count: > 0 } && historyModel is not null)
            {
                var headersIn = EnabledHeaders(historyModel);
                var context = new ScriptContext
                {
                    EventName = "prerequest",
                    RequestName = requestName,
                    Request = new ScriptRequest(historyModel.Method.ToString(), historyModel.Url, headersIn, historyModel.BodyText),
                    Scopes = scopes
                };
                var chain = scripts.PreRequest;
                preResult = await Task.Run(() => ScriptRunner.Run(context, chain, cts.Token));
                ApplyVariableChanges(owners, scopes, preResult);
                ShowScriptOutput(preResult, null);
                if (!preResult.Succeeded)
                {
                    // As in Postman, a failing pre-request script means the request isn't sent.
                    StatusText = $"Not sent - pre-request script failed: {preResult.Error}";
                    SelectedResponseTabIndex = TestsTabIndex;
                    return;
                }

                scopes = preResult.Scopes;
                if (!headersIn.SequenceEqual(preResult.RequestHeaders))
                    scriptedHeaders = preResult.RequestHeaders;
            }

            var variables = scopes.Merged();
            var prepared = isPreflight ? PreparePreflight(variables) : PrepareSend(variables, scriptedHeaders);
            var requestModel = prepared.Model;

            var response = (!isPreflight && SelectedRequestMode == RequestMode.RawSocket)
                ? await _rawSocketService.SendAsync(requestModel, cts.Token)
                : await _httpService.SendAsync(requestModel, cts.Token);

            // Pretty-printing a large body is real work; keep it off the UI thread.
            var prettyBody = await Task.Run(() => JsonDefaults.TryFormat(response.Body));

            CurrentResponse = response;
            RawResponseBody = response.Body;
            ResponseBody = prettyBody;
            ResponseHeaders = new ObservableCollection<KeyValuePair<string, string>>(response.Headers);
            SetDiagnostics(response.Diagnostics);
            if (response.Diagnostics != null)
                SelectedResponseTabIndex = ConnectionTabIndex;

            if (CorsEnabled)
            {
                CorsVerdict = CorsAnalyzer.Analyze(GetEnabledHeaderValue(requestModel, "Origin"), prepared.CorsMethod,
                    prepared.CorsRequestedHeaders, CorsIncludeCredentials, isPreflight, response);
                HasCorsVerdict = true;
            }

            ScriptResult? testResult = null;
            if (scripts is { Test.Count: > 0 })
            {
                var context = new ScriptContext
                {
                    EventName = "test",
                    RequestName = requestName,
                    Request = new ScriptRequest(requestModel.Method.ToString(), requestModel.Url, EnabledHeaders(requestModel), requestModel.BodyText),
                    Response = new ScriptResponse(response.StatusCode, response.ReasonPhrase, response.Duration.TotalMilliseconds,
                        response.ResponseSize, response.Headers, response.Body),
                    Scopes = scopes
                };
                var chain = scripts.Test;
                testResult = await Task.Run(() => ScriptRunner.Run(context, chain, cts.Token));
                ApplyVariableChanges(owners, scopes, testResult);
            }
            if (preResult is not null || testResult is not null)
                ShowScriptOutput(preResult, testResult);

            StatusText = $"{response.StatusCode} {response.ReasonPhrase} - {response.Duration.TotalMilliseconds:F0}ms";
            if (response.IsTruncated)
                StatusText += $" - body truncated at {ResponseText.MaxBodyBytes / (1024 * 1024)} MB";
            if (TestResults.Count > 0)
                StatusText += $" - tests {TestResults.Count(t => t.Passed)}/{TestResults.Count} passed";
            if (testResult is { Succeeded: false })
                StatusText += " - test script failed";
            if (prepared.Unresolved.Count > 0)
                StatusText += $" - no value for {PlaceholderNames(prepared.Unresolved)}";

            if (historyModel != null)
            {
                // Isolated so a failure here can't discard the response we already have.
                try
                {
                    var snapshot = _persistenceService.SerializeHistorySnapshot(historyModel, out var bodyOmitted);
                    _owner.AddHistoryItem(new HistoryItem
                    {
                        Timestamp = DateTime.Now,
                        Method = historyModel.Method,
                        Url = historyModel.Url,
                        StatusCode = response.StatusCode,
                        Duration = response.Duration,
                        RequestSnapshot = snapshot,
                        BodyOmitted = bodyOmitted,
                        SavedRequestId = ActiveSavedRequestId,
                        SavedRequestName = ActiveSavedRequestId.HasValue ? ActiveRequestName : null,
                        CollectionId = ActiveCollectionId,
                        CollectionItemId = ActiveCollectionItemId
                    });
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Record history", ex);
                    StatusText += " (not recorded in history)";
                }
            }
        }
        catch (HttpRequestException ex)
        {
            // The outer message is often just "...see inner exception" (TLS failures in
            // particular); the innermost one names the actual cause.
            var cause = ex.GetBaseException().Message;
            StatusText = ex.Message.Contains(cause, StringComparison.OrdinalIgnoreCase)
                ? $"Error: {ex.Message}"
                : $"Error: {ex.Message} {cause}";
            ClearResponseKeepingScripts(preResult);
        }
        catch (OperationCanceledException)
        {
            StatusText = cts.IsCancellationRequested ? "Request cancelled" : "Request timed out";
            ClearResponseKeepingScripts(preResult);
        }
        catch (RawSocketException ex)
        {
            ClearResponseKeepingScripts(preResult);
            SetDiagnostics(ex.Diagnostics);
            SelectedResponseTabIndex = ConnectionTabIndex;
            StatusText = $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            ClearResponseKeepingScripts(preResult);
        }
        finally
        {
            IsLoading = false;
            _cts = null;
            cts.Dispose();
            _owner.ScheduleTabSessionSave();
        }
    }

    // A failed send still shows what the pre-request script logged.
    private void ClearResponseKeepingScripts(ScriptResult? preResult)
    {
        ClearResponse();
        if (preResult is not null)
            ShowScriptOutput(preResult, null);
    }

    private bool TryPrepareUrl()
    {
        if (string.IsNullOrWhiteSpace(RequestUrl))
        {
            StatusText = "Please enter a URL";
            return false;
        }

        // With placeholders the URL can only be judged once variables are substituted
        // (RequireValidUrl), and "{{baseUrl}}/x" must not be rewritten to "https://{{baseUrl}}/x".
        if (VariableResolver.HasPlaceholders(RequestUrl))
            return true;

        var url = NormalizeUrl(RequestUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            StatusText = "Invalid URL";
            return false;
        }

        RequestUrl = url;
        return true;
    }

    // A URL typed without a scheme is treated as https, both when sending and in cURL export.
    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://" + url;
    }

    private bool ValidateTimeout()
    {
        if (IsRequestTimeoutValid())
            return true;

        StatusText = "Timeout must be between 1 and 300 seconds";
        return false;
    }

    [RelayCommand]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request finished between the null check and the cancel; nothing to do.
        }
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
            try
            {
                var request = BuildRequestModel();
                await _persistenceService.SaveRequestAsync(request, dialog.FileName);
                StatusText = $"Exported to {Path.GetFileName(dialog.FileName)}";
            }
            catch (Exception ex)
            {
                StatusText = $"Export failed: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void CopyResponseBody()
    {
        if (!string.IsNullOrEmpty(ResponseBody) && TrySetClipboard(ResponseBody))
            StatusText = "Response copied to clipboard";
    }

    // Clipboard.SetText throws (CLIPBRD_E_CANT_OPEN) when another process holds
    // the clipboard open — a transient condition that must not crash the app.
    private bool TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            StatusText = "Could not access the clipboard — try again";
            return false;
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
            StatusText = response.IsTruncated
                ? $"Downloaded {Path.GetFileName(dialog.FileName)} (body was truncated at {ResponseText.MaxBodyBytes / (1024 * 1024)} MB)"
                : $"Downloaded {Path.GetFileName(dialog.FileName)}";
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
        if (string.IsNullOrWhiteSpace(RequestUrl))
        {
            StatusText = "Please enter a URL";
            return;
        }

        CurlExportResult result;
        try
        {
            // Exactly what Send would put on the wire: variables substituted, CORS Origin
            // header included. (Pre-request scripts aren't run for an export.)
            var prepared = PrepareSend(CurrentVariableOwners().ToScopes().Merged(), scriptedHeaders: null);
            result = CurlExporter.Generate(prepared.Model, _owner.CurrentSettings.CurlExportStyle);
            if (prepared.Unresolved.Count > 0)
            {
                var note = $"no value for {PlaceholderNames(prepared.Unresolved)}";
                result = result with { Warning = result.Warning is null ? note : $"{result.Warning}; {note}" };
            }
        }
        catch (Exception ex) when (ex is UriFormatException or FormatException)
        {
            StatusText = $"Can't build curl command: {ex.Message}";
            return;
        }

        if (TrySetClipboard(result.Command))
        {
            StatusText = result.Warning is null
                ? "Curl command copied to clipboard"
                : $"Curl command copied - note: {result.Warning}";
        }
    }

    [RelayCommand]
    public async Task SaveCurrentRequestAsync()
    {
        try
        {
            // A tab opened from a collection saves back into that collection.
            if (ActiveCollectionItemId.HasValue && await _owner.Collections.SaveTabToItemAsync(this))
                return;

            var requestModel = BuildRequestModel();

            if (ActiveSavedRequestId.HasValue)
            {
                var existing = _owner.SavedRequests.FirstOrDefault(r => r.Id == ActiveSavedRequestId.Value);
                if (existing is null && !_owner.IsSavedLibraryLoaded)
                {
                    // The linked entry may simply not be loaded yet; saving now would create a duplicate.
                    StatusText = "Saved requests are still loading - try again in a moment";
                    return;
                }

                if (existing != null)
                {
                    var previous = existing.Request;
                    existing.Request = requestModel;
                    try
                    {
                        await _savedRequestService.SaveAsync(existing);
                    }
                    catch
                    {
                        // Keep the library entry matching what's on disk.
                        existing.Request = previous;
                        throw;
                    }

                    // The file now holds what the tab shows, so the load-time warning is spent.
                    existing.SecretsUnreadable = false;
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

            // Only link the tab and list the entry once it's actually on disk.
            await _savedRequestService.SaveAsync(saved);
            ActiveCollectionItemId = null;
            ActiveSavedRequestId = saved.Id;
            ActiveRequestName = saved.Name;
            _owner.SavedRequests.Insert(0, saved);
            IsDirty = false;
            StatusText = $"Saved \"{saved.Name}\"";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
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

    // ==================== Body: form fields and files ====================

    [RelayCommand]
    private void AddFormFieldRow()
    {
        FormFields.Add(new FormFieldItem());
    }

    [RelayCommand]
    private void RemoveFormFieldRow(FormFieldItem? item)
    {
        if (item is null)
            return;

        FormFields.Remove(item);
        if (FormFields.Count == 0)
            FormFields.Add(new FormFieldItem());
    }

    [RelayCommand]
    private void ChooseFormFieldFile(FormFieldItem? item)
    {
        if (item is null || ChooseFiles("Choose a file to upload", multiselect: false) is not [var path])
            return;

        item.Kind = FormFieldKind.File;
        item.FilePath = path;
        if (string.IsNullOrWhiteSpace(item.Key))
            item.Key = "file";
    }

    [RelayCommand]
    private void ClearFormFieldFile(FormFieldItem? item)
    {
        if (item != null)
            item.FilePath = string.Empty;
    }

    [RelayCommand]
    private void ChooseFormFiles()
    {
        var paths = ChooseFiles("Choose files to upload", multiselect: true);
        if (paths.Length > 0)
            AddFilesToForm(paths);
    }

    [RelayCommand]
    private void ChooseBinaryFile()
    {
        if (ChooseFiles("Choose the file to send as the body", multiselect: false) is [var path])
            BinaryFilePath = path;
    }

    [RelayCommand]
    private void ClearBinaryFile()
    {
        BinaryFilePath = string.Empty;
    }

    /// <summary>Files dropped from Explorer: form-data adds file fields; binary uses the first file.</summary>
    public void AddDroppedFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        var skipped = paths.Count - files.Count;
        var skippedNote = skipped == 0 ? string.Empty : $" ({skipped} folder{(skipped == 1 ? "" : "s")} skipped)";
        if (files.Count == 0)
        {
            StatusText = "Folders can't be uploaded. Drop files instead.";
            return;
        }

        if (SelectedBodyMode == BodyMode.Binary)
        {
            BinaryFilePath = files[0];
            StatusText = files.Count == 1
                ? $"Sending {Path.GetFileName(files[0])} as the body{skippedNote}"
                : $"Sending {Path.GetFileName(files[0])} as the body; a binary body holds one file, so the other {files.Count - 1} were ignored{skippedNote}";
            return;
        }

        AddFilesToForm(files);
        StatusText = $"Added {files.Count} file{(files.Count == 1 ? "" : "s")} to the form{skippedNote}";
    }

    private void AddFilesToForm(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            // Fill the empty row at the end first, then append.
            var row = FormFields.Count > 0 && FormFields[^1].IsBlank ? FormFields[^1] : null;
            if (row is null)
            {
                row = new FormFieldItem();
                FormFields.Add(row);
            }

            row.Key = "file";
            row.Kind = FormFieldKind.File;
            row.FilePath = path;
        }
    }

    private static string[] ChooseFiles(string title, bool multiselect)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "All Files (*.*)|*.*",
            Multiselect = multiselect,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    // ---- What the Body tab shows about the current body ----

    public bool IsBodyIgnoredForMethod =>
        SelectedBodyMode != BodyMode.None && SelectedMethod is HttpMethodType.GET or HttpMethodType.HEAD;

    public string BodyIgnoredText =>
        $"{SelectedMethod} requests are sent without a body. Switch to POST, PUT or PATCH to send it.";

    /// <summary>The Content-Type the body goes out with (as the composer decides it), or empty.</summary>
    public string EffectiveContentType
    {
        get
        {
            if (SelectedBodyMode == BodyMode.None || IsBodyIgnoredForMethod)
                return string.Empty;
            if (SelectedBodyMode == BodyMode.FormData)
                return "multipart/form-data";   // the boundary is generated per send
            return ContentTypeHeaderRow()
                   ?? (SelectedBodyMode == BodyMode.Binary
                       ? MimeTypes.FromFileName(BinaryFilePath)
                       : RequestComposer.DefaultContentType(SelectedBodyMode));
        }
    }

    /// <summary>The URL-encoded body exactly as it will be sent (same code path as the composer).</summary>
    public string UrlEncodedPreview => FormUrlEncoding.Serialize(RequestComposer.UrlEncodedPairs(new RequestModel
    {
        FormFields = FormFields.Select(f => f.ToData()).ToList()
    }));

    public string IgnoredFileFieldsText
    {
        get
        {
            var count = FormFields.Count(f => f.IsEnabled && f.IsFile && !string.IsNullOrWhiteSpace(f.Key));
            return count switch
            {
                0 => string.Empty,
                1 => "1 file field isn't sent with URL-encoded. Switch to Form-data to upload it.",
                _ => $"{count} file fields aren't sent with URL-encoded. Switch to Form-data to upload them."
            };
        }
    }

    public string FormDataSummary
    {
        get
        {
            var sent = FormFields.Where(f => f.IsEnabled && !string.IsNullOrWhiteSpace(f.Key)).ToList();
            var unnamed = FormFields.Count(f => f.IsEnabled && string.IsNullOrWhiteSpace(f.Key) && (f.HasFile || !string.IsNullOrWhiteSpace(f.Value)));
            var files = sent.Where(f => f.IsFile).ToList();
            var missing = files.Count(f => !f.HasFile || f.FileMissing);

            var text = (sent.Count, files.Count) switch
            {
                (0, _) => "Nothing to send yet. Add a text field or drop files below.",
                (var n, 0) => n == 1 ? "1 text field" : $"{n} text fields",
                (var n, var f) when n == f => $"{Plural(f, "file")} ({ByteSize.Format(files.Sum(x => x.FileSize ?? 0))}{(f == 1 ? "" : " in total")})",
                (var n, var f) => $"{Plural(n, "part")}, including {Plural(f, "file")} ({ByteSize.Format(files.Sum(x => x.FileSize ?? 0))})"
            };
            if (missing > 0)
                text += missing == 1 ? ". 1 file is missing or not chosen" : $". {missing} files are missing or not chosen";
            if (unnamed > 0)
                text += unnamed == 1 ? ". 1 row has no name, so it isn't sent" : $". {unnamed} rows have no name, so they aren't sent";
            return text;
        }
    }

    public bool BinaryHasFile => !string.IsNullOrWhiteSpace(BinaryFilePath);
    public string BinaryFileName => Path.GetFileName(BinaryFilePath);
    public string BinaryExtensionLabel => FormFieldItem.ExtensionBadge(BinaryFilePath);
    public bool BinaryFileMissing => BinaryHasFile && _binaryFileSize is null;
    public string BinaryFileSizeText => _binaryFileSize is { } size ? ByteSize.Format(size) : string.Empty;

    public string BinaryContentType => ContentTypeHeaderRow() ?? MimeTypes.FromFileName(BinaryFilePath);

    public string BinaryContentTypeSource => ContentTypeHeaderRow() is null
        ? "detected from the file name"
        : "set in Headers";

    private long? _binaryFileSize;

    private void RefreshBinaryFileInfo()
    {
        try
        {
            var info = BinaryHasFile ? new FileInfo(BinaryFilePath) : null;
            _binaryFileSize = info is { Exists: true } ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _binaryFileSize = null;
        }
    }

    private string? ContentTypeHeaderRow() =>
        RequestHeaders.FirstOrDefault(h => h.IsEnabled && h.Key.Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() is { Length: > 0 } value ? value : null;

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private void RefreshBodySummaries()
    {
        OnPropertyChanged(nameof(IsBodyIgnoredForMethod));
        OnPropertyChanged(nameof(BodyIgnoredText));
        OnPropertyChanged(nameof(EffectiveContentType));
        OnPropertyChanged(nameof(UrlEncodedPreview));
        OnPropertyChanged(nameof(IgnoredFileFieldsText));
        OnPropertyChanged(nameof(FormDataSummary));
        OnPropertyChanged(nameof(BinaryHasFile));
        OnPropertyChanged(nameof(BinaryFileName));
        OnPropertyChanged(nameof(BinaryExtensionLabel));
        OnPropertyChanged(nameof(BinaryFileMissing));
        OnPropertyChanged(nameof(BinaryFileSizeText));
        OnPropertyChanged(nameof(BinaryContentType));
        OnPropertyChanged(nameof(BinaryContentTypeSource));
    }

    public RequestModel BuildRequestModel()
    {
        return new RequestModel
        {
            FormatVersion = RequestModel.CurrentFormatVersion,
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
            FormFields = FormFields
                .Where(f => !string.IsNullOrWhiteSpace(f.Key) || f.HasFile)
                .Select(f => f.ToData())
                .ToList(),
            BinaryFilePath = BinaryFilePath,
            AuthMode = SelectedAuthMode,
            AuthToken = AuthToken,
            AuthUsername = AuthUsername,
            AuthPassword = AuthPassword,
            ApiKeyHeader = ApiKeyHeader,
            ApiKeyValue = ApiKeyValue,
            TimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 1, 300),
            FollowRedirects = RequestFollowRedirects,
            CorsEnabled = CorsEnabled,
            CorsOrigin = CorsOrigin,
            CorsRequestMethod = CorsRequestMethod,
            CorsRequestHeaders = CorsRequestHeaders,
            CorsIncludeCredentials = CorsIncludeCredentials,
            PreRequestScript = PreRequestScript,
            TestScript = TestScript
        };
    }

    /// <summary>
    /// Builds the model that is actually sent over the wire: the plain request plus the
    /// injected CORS <c>Origin</c> header when CORS testing is enabled. The injection lives here
    /// (not in <see cref="BuildRequestModel"/>) so saved/exported requests keep a clean Headers list.
    /// </summary>
    /// <param name="scriptedHeaders">The header list a pre-request script left, when it changed it.</param>
    private RequestModel BuildSendModel(IReadOnlyList<KeyValuePair<string, string>>? scriptedHeaders = null)
    {
        var model = BuildRequestModel();
        if (scriptedHeaders is not null)
            model.Headers = scriptedHeaders.Select(h => new HeaderItemData { Key = h.Key, Value = h.Value }).ToList();

        if (CorsEnabled && !string.IsNullOrWhiteSpace(CorsOrigin))
            SetHeader(model, "Origin", CorsOrigin.Trim());

        return model;
    }

    private RequestModel BuildPreflightModel(RequestModel baseModel, string method, string requestedHeaders)
    {
        var model = new RequestModel
        {
            Method = HttpMethodType.OPTIONS,
            Url = baseModel.Url,
            QueryParams = baseModel.QueryParams
                .Select(p => new QueryParamData { Key = p.Key, Value = p.Value, IsEnabled = p.IsEnabled })
                .ToList(),
            BodyMode = BodyMode.None,
            BodyText = string.Empty,
            AuthMode = AuthMode.None,
            TimeoutSeconds = baseModel.TimeoutSeconds,
            // A browser never follows a redirect on a preflight: it fails the request. Show
            // the 3xx itself, which the analysis reports as a failed preflight status.
            FollowRedirects = false,
            CorsEnabled = CorsEnabled,
            CorsOrigin = baseModel.CorsOrigin,
            CorsRequestMethod = CorsRequestMethod,
            CorsRequestHeaders = baseModel.CorsRequestHeaders,
            CorsIncludeCredentials = CorsIncludeCredentials
        };

        var origin = !string.IsNullOrWhiteSpace(baseModel.CorsOrigin)
            ? baseModel.CorsOrigin.Trim()
            : GetEnabledHeaderValue(baseModel, "Origin");
        if (!string.IsNullOrWhiteSpace(origin))
            SetHeader(model, "Origin", origin);

        SetHeader(model, "Access-Control-Request-Method", method);
        if (!string.IsNullOrWhiteSpace(requestedHeaders))
            SetHeader(model, "Access-Control-Request-Headers", requestedHeaders);

        return model;
    }

    private static void SetHeader(RequestModel model, string key, string value)
    {
        model.Headers.RemoveAll(h => h.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        model.Headers.Add(new HeaderItemData { Key = key, Value = value, IsEnabled = true });
    }

    private static string GetEnabledHeaderValue(RequestModel model, string key)
    {
        return model.Headers
            .FirstOrDefault(h => h.IsEnabled && h.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? string.Empty;
    }

    // The Access-Control-Request-Headers a browser would send: every header of the request
    // as composed for the wire (Headers rows, auth, effective Content-Type) that is neither
    // CORS-safelisted nor controlled by the browser itself.
    /// <exception cref="FormatException">A header or the URL is invalid.</exception>
    private string BuildCorsRequestedHeaders(RequestModel model, string? methodOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(model.CorsRequestHeaders))
            return model.CorsRequestHeaders.Trim();

        var composed = RequestComposer.Compose(model, includeClientDefaults: false, methodOverride, ComposeOptions.Describe);
        var headers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in composed.Headers)
        {
            if (!IsBrowserControlledHeader(key) && !IsCorsSafelistedHeader(key, value))
                headers.Add(key.ToLowerInvariant());
        }

        return string.Join(", ", headers);
    }

    // Headers page scripts can't set (the Fetch "forbidden request-header" names, plus
    // User-Agent which browsers ignore), so they never appear in a preflight's list.
    private static readonly HashSet<string> BrowserControlledHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept-Charset", "Accept-Encoding", "Connection", "Content-Length", "Cookie", "Cookie2",
        "Date", "DNT", "Expect", "Host", "Keep-Alive", "Origin", "Referer", "Set-Cookie", "TE",
        "Trailer", "Transfer-Encoding", "Upgrade", "User-Agent", "Via"
    };

    private static bool IsBrowserControlledHeader(string key)
    {
        key = key.Trim();
        return BrowserControlledHeaders.Contains(key) ||
               key.StartsWith("Access-Control-Request-", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCorsSafelistedHeader(string key, string value)
    {
        key = key.Trim();
        if (key.Equals("Accept", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Content-Language", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
               IsCorsSafelistedContentType(value);
    }

    private static bool IsCorsSafelistedContentType(string value)
    {
        var contentType = (value ?? string.Empty).Split(';', 2)[0].Trim();
        return contentType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
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

        FormFields.Clear();
        foreach (var field in request.FormFields)
            FormFields.Add(FormFieldItem.FromData(field));
        if (FormFields.Count == 0)
            FormFields.Add(new FormFieldItem());
        BinaryFilePath = request.BinaryFilePath;

        SelectedBodyMode = request.BodyMode;
        RequestBody = request.BodyText;
        SelectedAuthMode = request.AuthMode;
        AuthToken = request.AuthToken;
        AuthUsername = request.AuthUsername;
        AuthPassword = request.AuthPassword;
        ApiKeyHeader = string.IsNullOrWhiteSpace(request.ApiKeyHeader) ? RequestComposer.DefaultApiKeyHeader : request.ApiKeyHeader;
        ApiKeyValue = request.ApiKeyValue;
        RequestTimeoutSeconds = request.TimeoutSeconds is >= 1 and <= 300
            ? request.TimeoutSeconds
            : _owner.CurrentSettings.DefaultTimeoutSeconds;
        RequestFollowRedirects = request.FollowRedirects;
        CorsEnabled = request.CorsEnabled;
        CorsOrigin = request.CorsOrigin;
        CorsRequestMethod = request.CorsRequestMethod;
        CorsRequestHeaders = request.CorsRequestHeaders;
        CorsIncludeCredentials = request.CorsIncludeCredentials;
        PreRequestScript = request.PreRequestScript;
        TestScript = request.TestScript;
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
        CorsVerdict = null;
        HasCorsVerdict = false;
        TestResults = [];
        ScriptConsoleText = string.Empty;
        ScriptError = string.Empty;
        TestsTabHeader = "Tests";
        HasScriptOutput = false;
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
        if (response.GetHeader("Content-Disposition") is not { } contentDisposition)
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
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = response.GetHeader("Content-Type") ?? string.Empty;

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

    private void MarkRequestChanged(bool refreshTitle = false)
    {
        if (_suppressChangeNotifications)
            return;

        RefreshBodySummaries();
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
        RefreshBodySummaries();
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
        // A form row's display-only properties (file size, missing flag, labels) change the
        // summary but not the request, so they mustn't mark the tab as edited.
        if (sender is FormFieldItem && e.PropertyName is { } name && !FormFieldItem.PersistedProperties.Contains(name))
        {
            RefreshBodySummaries();
            return;
        }

        MarkRequestChanged();
    }
}
