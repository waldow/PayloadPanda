using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.ViewModels;

/// <summary>An entry of the title bar's environment dropdown; <see cref="Id"/> is null for "No environment".</summary>
public sealed record EnvironmentChoice(Guid? Id, string Name)
{
    // What the dropdown shows and what screen readers announce.
    public override string ToString() => Name;
}

/// <summary>
/// The environments, the one that is active, and the globals. As with collections, the
/// operations take no dialogs so they can be driven without a UI; the command adds the window.
/// </summary>
public partial class EnvironmentsViewModel : ObservableObject
{
    private static readonly EnvironmentChoice s_none = new(null, "No environment");

    private readonly MainViewModel _owner;
    private readonly EnvironmentService _service;
    private bool _rebuildingChoices;

    public EnvironmentsViewModel(MainViewModel owner, EnvironmentService service)
    {
        _owner = owner;
        _service = service;
        Choices.Add(s_none);
        _selectedChoice = s_none;
    }

    public ObservableCollection<EnvironmentModel> Items { get; } = [];

    /// <summary>Always-on variables underneath every environment and collection.</summary>
    public EnvironmentModel Globals { get; private set; } = new() { Name = "Globals" };

    public ObservableCollection<EnvironmentChoice> Choices { get; }  = [];

    [ObservableProperty]
    private EnvironmentChoice? _selectedChoice;

    /// <summary>The environment whose variables apply to every request; null when none is selected.</summary>
    public EnvironmentModel? Active =>
        SelectedChoice?.Id is { } id ? Items.FirstOrDefault(e => e.Id == id) : null;

    /// <summary>Set when an operation couldn't be saved.</summary>
    public string LastError { get; private set; } = string.Empty;

    partial void OnSelectedChoiceChanged(EnvironmentChoice? value)
    {
        // While the list is rebuilt the dropdown briefly has no selection; that isn't a choice.
        if (_rebuildingChoices)
            return;

        _owner.RememberActiveEnvironment(value?.Id);
        RefreshTabs();
    }

    public async Task LoadFromDiskAsync()
    {
        Globals = await _service.LoadGlobalsAsync();
        foreach (var environment in await _service.LoadAllAsync())
            Items.Add(environment);
        RebuildChoices(_owner.CurrentSettings.ActiveEnvironmentId);
    }

    public void Activate(Guid? id) =>
        SelectedChoice = Choices.FirstOrDefault(c => c.Id == id) ?? s_none;

    // ==================== Operations ====================

    public async Task<bool> AddAsync(EnvironmentModel environment)
    {
        if (!await TrySaveAsync(environment))
            return false;

        Items.Add(environment);
        RebuildChoices(SelectedChoice?.Id);
        return true;
    }

    /// <summary>Adds the environment in a Postman file, or merges a Postman globals file into the globals.</summary>
    /// <exception cref="FormatException">The text isn't a Postman environment.</exception>
    public async Task<PostmanEnvironmentImportResult?> ImportJsonAsync(string json)
    {
        var result = PostmanEnvironmentConverter.Import(json);
        if (!result.IsGlobals)
            return await AddAsync(result.Environment) ? result : null;

        MergeInto(Globals, result.Environment);
        if (!await TrySaveAsync(Globals))
            return null;
        RefreshTabs();
        return result;
    }

    /// <summary>Same keys are overwritten, new ones added; nothing is removed.</summary>
    public static void MergeInto(EnvironmentModel target, EnvironmentModel source)
    {
        foreach (var incoming in source.Variables)
        {
            var (variable, _) = target.Variables.SetValue(incoming.Key.Trim(), incoming.Value);
            variable.IsEnabled = incoming.IsEnabled;
            variable.IsSecret |= incoming.IsSecret;
        }
    }

    /// <summary>Takes over the edited globals and environment list (as the Environments window returns them).</summary>
    public async Task<bool> ApplyEditsAsync(EnvironmentModel globals, IReadOnlyList<EnvironmentModel> environments)
    {
        var ok = await TrySaveAsync(globals, isGlobals: true);
        foreach (var environment in environments)
            ok &= await TrySaveAsync(environment);

        var kept = environments.Select(e => e.Id).ToHashSet();
        foreach (var removed in Items.Where(e => !kept.Contains(e.Id)).ToList())
        {
            try
            {
                _service.Delete(removed.Id);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write("Delete environment", ex);
                LastError = $"Could not delete \"{removed.Name}\": {ex.Message}";
                ok = false;
            }
        }

        var activeId = SelectedChoice?.Id;
        Globals = globals;
        Items.Clear();
        foreach (var environment in environments.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(environment);
        RebuildChoices(activeId);
        RefreshTabs();
        return ok;
    }

    /// <summary>Saves after a script changed variables; failures go to the log.</summary>
    public void SaveInBackground(EnvironmentModel environment) => _ = TrySaveAsync(environment);

    private Task<bool> TrySaveAsync(EnvironmentModel environment) =>
        TrySaveAsync(environment, ReferenceEquals(environment, Globals));

    private async Task<bool> TrySaveAsync(EnvironmentModel environment, bool isGlobals)
    {
        try
        {
            // The live model keeps changing on the UI thread; write a snapshot off it.
            var snapshot = environment.Clone();
            await Task.Run(() => isGlobals ? _service.SaveGlobalsAsync(snapshot) : _service.SaveAsync(snapshot));
            environment.SecretsUnreadable = false;
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Save environment", ex);
            LastError = $"Could not save \"{environment.Name}\": {ex.Message}";
            return false;
        }
    }

    // Keeps the dropdown in step with the list. A selection that no longer exists falls back to none.
    private void RebuildChoices(Guid? select)
    {
        _rebuildingChoices = true;
        Choices.Clear();
        Choices.Add(s_none);
        foreach (var environment in Items.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            Choices.Add(new EnvironmentChoice(environment.Id, environment.Name));
        SelectedChoice = Choices.FirstOrDefault(c => c.Id == select) ?? s_none;
        _rebuildingChoices = false;

        OnPropertyChanged(nameof(SelectedChoice));
        if (SelectedChoice.Id != select)
            _owner.RememberActiveEnvironment(SelectedChoice.Id);
        // Tabs restored before the environments loaded still describe the old scopes.
        RefreshTabs();
    }

    private void RefreshTabs()
    {
        foreach (var tab in _owner.Tabs)
            tab.RefreshVariableScope();
    }

    // ==================== Commands ====================

    [RelayCommand]
    private async Task ManageAsync()
    {
        var dialog = new Views.EnvironmentsWindow(Globals.Clone(), Items.Select(e => e.Clone()).ToList(), Active?.Id)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        if (!await ApplyEditsAsync(dialog.Globals, dialog.Environments))
        {
            MessageBox.Show(LastError, "Environments", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
