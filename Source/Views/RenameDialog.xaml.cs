using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PayloadPanda.Views;

/// <summary>Asks for a name: renaming something, or naming a new collection or folder.</summary>
public partial class RenameDialog : Window
{
    public string NewName => NameBox.Text.Trim();

    /// <param name="title">What is being named, e.g. "Rename request" or "New collection".</param>
    /// <param name="action">The confirm button's label: what happens on OK.</param>
    public RenameDialog(string currentName, string title = "Rename request", string action = "Rename")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        ActionButton.Content = action;
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Confirm()
    {
        if (NewName.Length == 0)
            return;

        DialogResult = true;
        Close();
    }

    private void OK_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // A name is required, so the action is only offered once there is one.
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ActionButton is not null)
            ActionButton.IsEnabled = NewName.Length > 0;
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    // No title bar, so the card itself drags the window.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
