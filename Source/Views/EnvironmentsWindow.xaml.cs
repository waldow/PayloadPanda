using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using PayloadPanda.Models;
using PayloadPanda.Services;
using PayloadPanda.ViewModels;

namespace PayloadPanda.Views;

/// <summary>A row of the list on the left: the globals or one environment.</summary>
public partial class EnvironmentRow(EnvironmentModel model, bool isGlobals) : ObservableObject
{
    public EnvironmentModel Model { get; } = model;
    public bool IsGlobals { get; } = isGlobals;

    [ObservableProperty]
    private string _name = model.Name;

    [ObservableProperty]
    private string _note = string.Empty;
}

/// <summary>
/// Edits the globals and the environments. Works on the copies it is given; the caller
/// applies <see cref="Globals"/> and <see cref="Environments"/> when the dialog returns true.
/// </summary>
public partial class EnvironmentsWindow : Window
{
    private readonly ObservableCollection<EnvironmentRow> _rows = [];
    private readonly Guid? _activeId;
    private EnvironmentRow? _current;
    private bool _loadingRow;

    public EnvironmentsWindow(EnvironmentModel globals, IReadOnlyList<EnvironmentModel> environments, Guid? activeId)
    {
        InitializeComponent();
        _activeId = activeId;

        _rows.Add(new EnvironmentRow(globals, isGlobals: true) { Name = "Globals", Note = "Always on" });
        foreach (var environment in environments)
            _rows.Add(NewRow(environment));
        SetsList.ItemsSource = _rows;
        SetsList.SelectedItem = _rows.FirstOrDefault(r => !r.IsGlobals && r.Model.Id == activeId)
                                ?? _rows.FirstOrDefault(r => !r.IsGlobals)
                                ?? _rows[0];
    }

    public EnvironmentModel Globals => _rows[0].Model;
    public List<EnvironmentModel> Environments => _rows.Where(r => !r.IsGlobals).Select(r => r.Model).ToList();

    private EnvironmentRow NewRow(EnvironmentModel environment) =>
        new(environment, isGlobals: false) { Note = environment.Id == _activeId ? "Active" : string.Empty };

    // ---- selection ----

    private void SetsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SetsList.SelectedItem is not EnvironmentRow row || ReferenceEquals(row, _current))
            return;

        CommitCurrent();
        _current = row;

        _loadingRow = true;
        NameBox.Text = row.Name;
        _loadingRow = false;
        NameBox.IsEnabled = !row.IsGlobals;
        DuplicateButton.IsEnabled = !row.IsGlobals;
        DeleteButton.IsEnabled = !row.IsGlobals;
        HintText.Text = row.IsGlobals
            ? "Globals apply to every request. An environment or a collection with the same variable takes precedence."
            : "Applies to every request while it is selected in the title bar. Its values take precedence over a collection's and the globals'.";
        Variables.Load(row.Model.Variables);
    }

    // Writes what is on screen back into the row being left (or saved).
    private void CommitCurrent()
    {
        if (_current is null)
            return;

        _current.Model.Variables = Variables.Commit();
        if (!_current.IsGlobals && NameBox.Text.Trim() is { Length: > 0 } name)
        {
            _current.Model.Name = name;
            _current.Name = name;
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The list follows the name as it is typed.
        if (!_loadingRow && _current is { IsGlobals: false } row && NameBox.Text.Trim() is { Length: > 0 } name)
            row.Name = name;
    }

    // ---- list actions ----

    private void Add(EnvironmentModel environment)
    {
        var row = NewRow(environment);
        _rows.Add(row);
        SetsList.SelectedItem = row;
        SetsList.ScrollIntoView(row);
    }

    private string UniqueName(string wanted)
    {
        var name = wanted;
        for (var n = 2; _rows.Any(r => r.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)); n++)
            name = $"{wanted} {n}";
        return name;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        Add(new EnvironmentModel { Name = UniqueName("New environment") });
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsGlobals: false } row)
            return;

        CommitCurrent();
        var copy = row.Model.Clone();
        copy.Id = Guid.NewGuid();
        copy.CreatedAt = DateTime.Now;
        copy.Name = UniqueName(row.Model.Name + " copy");
        Add(copy);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsGlobals: false } row)
            return;

        var confirm = MessageBox.Show(this, $"Delete the environment \"{row.Name}\"? It is removed when you save.", "Delete environment",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        var index = _rows.IndexOf(row);
        _current = null; // nothing of it left to commit
        _rows.Remove(row);
        SetsList.SelectedItem = _rows[Math.Min(index, _rows.Count - 1)];
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Postman environment",
            Filter = "Postman environment (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var (imported, isGlobals) = PostmanEnvironmentConverter.Import(File.ReadAllText(dialog.FileName));
            if (!isGlobals)
            {
                imported.Name = UniqueName(imported.Name);
                Add(imported);
                return;
            }

            // A globals file adds to the globals rather than becoming an environment.
            CommitCurrent();
            EnvironmentsViewModel.MergeInto(Globals, imported);
            _current = null;
            SetsList.SelectedItem = null;
            SetsList.SelectedItem = _rows[0];
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not import the file: {ex.Message}", "Import environment",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } row)
            return;

        CommitCurrent();
        var safeName = string.Join("_", row.Model.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dialog = new SaveFileDialog
        {
            Title = "Export as Postman environment",
            Filter = "Postman environment (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = (safeName.Length > 0 ? safeName : "environment") + (row.IsGlobals ? ".postman_globals.json" : ".postman_environment.json")
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, PostmanEnvironmentConverter.Export(row.Model, row.IsGlobals));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(this, $"Could not export: {ex.Message}", "Export environment",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- footer ----

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitCurrent();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
