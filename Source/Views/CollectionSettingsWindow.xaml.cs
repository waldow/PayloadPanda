using System.Windows;
using PayloadPanda.Models;

namespace PayloadPanda.Views;

/// <summary>
/// Edits a collection's name, description, variables and collection-level scripts. Works on
/// the copy it is given; the caller applies it when the dialog returns true.
/// </summary>
public partial class CollectionSettingsWindow : Window
{
    private readonly CollectionModel _collection;

    public CollectionSettingsWindow(CollectionModel editCopy)
    {
        InitializeComponent();
        _collection = editCopy;

        NameBox.Text = editCopy.Name;
        DescriptionBox.Text = editCopy.Description;
        PreRequestBox.Text = editCopy.PreRequestScript;
        TestBox.Text = editCopy.TestScript;
        Variables.Load(editCopy.Variables);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var variables = Variables.Commit();
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("The collection needs a name.", "Collection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _collection.Name = NameBox.Text.Trim();
        _collection.Description = DescriptionBox.Text;
        _collection.PreRequestScript = PreRequestBox.Text;
        _collection.TestScript = TestBox.Text;
        _collection.Variables = variables;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
