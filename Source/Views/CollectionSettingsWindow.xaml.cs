using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PayloadPanda.Models;

namespace PayloadPanda.Views;

/// <summary>
/// Edits a collection's name, description, variables and collection-level scripts. Works on
/// the copy it is given; the caller applies it when the dialog returns true.
/// </summary>
public partial class CollectionSettingsWindow : Window
{
    private readonly CollectionModel _collection;
    private readonly ObservableCollection<CollectionVariable> _variables;

    public CollectionSettingsWindow(CollectionModel editCopy)
    {
        InitializeComponent();
        _collection = editCopy;

        NameBox.Text = editCopy.Name;
        DescriptionBox.Text = editCopy.Description;
        PreRequestBox.Text = editCopy.PreRequestScript;
        TestBox.Text = editCopy.TestScript;
        _variables = new ObservableCollection<CollectionVariable>(editCopy.Variables);
        VariablesGrid.ItemsSource = _variables;
    }

    private void AddVariable_Click(object sender, RoutedEventArgs e)
    {
        _variables.Add(new CollectionVariable());
        VariablesGrid.ScrollIntoView(_variables[^1]);
    }

    private void RemoveVariable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CollectionVariable variable })
            _variables.Remove(variable);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // A cell still being edited holds its text until the edit is committed.
        VariablesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("The collection needs a name.", "Collection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _collection.Name = NameBox.Text.Trim();
        _collection.Description = DescriptionBox.Text;
        _collection.PreRequestScript = PreRequestBox.Text;
        _collection.TestScript = TestBox.Text;
        _collection.Variables = _variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Key))
            .Select(v => new CollectionVariable { Key = v.Key.Trim(), Value = v.Value ?? string.Empty, IsEnabled = v.IsEnabled, IsSecret = v.IsSecret })
            .ToList();

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
