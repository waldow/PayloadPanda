using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Highlighting;
using PayloadPanda.ViewModels;

namespace PayloadPanda.Views;

public partial class RequestWorkspaceView : UserControl
{
    private RequestWorkspaceViewModel? _viewModel;
    private bool _syncingEditorText;

    public RequestWorkspaceView()
    {
        InitializeComponent();

        var jsonHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
        RequestBodyEditor.SyntaxHighlighting = jsonHighlighting;
        ResponsePrettyEditor.SyntaxHighlighting = jsonHighlighting;

        RequestBodyEditor.TextChanged += RequestBodyEditor_TextChanged;
        DataContextChanged += RequestWorkspaceView_DataContextChanged;
    }

    private void RequestWorkspaceView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _viewModel = e.NewValue as RequestWorkspaceViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            SyncEditorsFromViewModel();
        }
        else
        {
            RequestBodyEditor.Text = string.Empty;
            ResponsePrettyEditor.Text = string.Empty;
        }
    }

    private void RequestBodyEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditorText || _viewModel is null)
            return;

        _viewModel.RequestBody = RequestBodyEditor.Text;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
            return;

        if (e.PropertyName == nameof(RequestWorkspaceViewModel.RequestBody))
        {
            if (RequestBodyEditor.Text != _viewModel.RequestBody)
            {
                _syncingEditorText = true;
                RequestBodyEditor.Text = _viewModel.RequestBody;
                _syncingEditorText = false;
            }
        }
        else if (e.PropertyName == nameof(RequestWorkspaceViewModel.ResponseBody))
        {
            ResponsePrettyEditor.Text = _viewModel.ResponseBody;
        }
    }

    private void SyncEditorsFromViewModel()
    {
        if (_viewModel is null)
            return;

        _syncingEditorText = true;
        RequestBodyEditor.Text = _viewModel.RequestBody;
        _syncingEditorText = false;
        ResponsePrettyEditor.Text = _viewModel.ResponseBody;
    }
}
