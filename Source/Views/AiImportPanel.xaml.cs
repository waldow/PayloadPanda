using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Highlighting;

namespace PayloadPanda.Views;

public partial class AiImportPanel : UserControl
{
    public AiImportPanel()
    {
        InitializeComponent();

        AiPreviewEditor.SyntaxHighlighting =
            HighlightingManager.Instance.GetDefinition("JavaScript");

        Loaded += AiImportPanel_Loaded;
        Unloaded += AiImportPanel_Unloaded;
    }

    private void AiImportPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is INotifyPropertyChanged vm)
            vm.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void AiImportPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is INotifyPropertyChanged vm)
            vm.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.AiImportPreviewJson))
        {
            var viewModel = (ViewModels.MainViewModel)DataContext;
            AiPreviewEditor.Text = viewModel.AiImportPreviewJson;
        }
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only close if clicking the backdrop itself, not the card
        if (e.OriginalSource == sender)
        {
            var viewModel = DataContext as ViewModels.MainViewModel;
            viewModel?.CloseAiImportCommand.Execute(null);
        }
    }
}
