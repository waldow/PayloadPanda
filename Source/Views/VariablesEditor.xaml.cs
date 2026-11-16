using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PayloadPanda.Models;

namespace PayloadPanda.Views;

/// <summary>
/// The variables grid shared by the collection and environments windows: On, Variable,
/// Value, Secret. Works on its own copies; <see cref="Commit"/> returns the edited list.
/// </summary>
public partial class VariablesEditor : UserControl
{
    private readonly ObservableCollection<CollectionVariable> _rows = [];

    public VariablesEditor()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
    }

    public void Load(IEnumerable<CollectionVariable> variables)
    {
        Grid.CancelEdit(DataGridEditingUnit.Row);
        _rows.Clear();
        foreach (var variable in variables)
            _rows.Add(variable.Clone());
    }

    /// <summary>The rows as edited, without rows that have no name.</summary>
    public List<CollectionVariable> Commit()
    {
        // A cell still being edited holds its text until the edit is committed.
        Grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        return _rows
            .Where(v => !string.IsNullOrWhiteSpace(v.Key))
            .Select(v => new CollectionVariable { Key = v.Key.Trim(), Value = v.Value ?? string.Empty, IsEnabled = v.IsEnabled, IsSecret = v.IsSecret })
            .ToList();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new CollectionVariable());
        Grid.ScrollIntoView(_rows[^1]);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CollectionVariable variable })
            _rows.Remove(variable);
    }
}
