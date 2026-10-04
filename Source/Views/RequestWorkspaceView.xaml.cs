using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using PayloadPanda.Models;
using PayloadPanda.ViewModels;

namespace PayloadPanda.Views;

public partial class RequestWorkspaceView : UserControl
{
    private RequestWorkspaceViewModel? _viewModel;
    private bool _syncingEditorText;

    public RequestWorkspaceView()
    {
        InitializeComponent();

        ResponsePrettyEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");

        RequestBodyEditor.TextChanged += RequestBodyEditor_TextChanged;
        DataContextChanged += RequestWorkspaceView_DataContextChanged;
    }

    private void RequestWorkspaceView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

        // The view is shared by every tab: never carry a drag highlight across tabs.
        SetDropHighlight(null);
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
            ResponseRawEditor.Text = string.Empty;
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
        else if (e.PropertyName == nameof(RequestWorkspaceViewModel.RawResponseBody))
        {
            ResponseRawEditor.Text = _viewModel.RawResponseBody;
        }
        else if (e.PropertyName == nameof(RequestWorkspaceViewModel.SelectedBodyMode))
        {
            ApplyBodyHighlighting();
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
        ResponseRawEditor.Text = _viewModel.RawResponseBody;
        ApplyBodyHighlighting();
    }

    // AvalonEdit ships "Json" and "XML" definitions; plain text gets none.
    private void ApplyBodyHighlighting()
    {
        RequestBodyEditor.SyntaxHighlighting = _viewModel?.SelectedBodyMode switch
        {
            BodyMode.Json => HighlightingManager.Instance.GetDefinition("Json")
                             ?? HighlightingManager.Instance.GetDefinition("JavaScript"),
            BodyMode.Xml => HighlightingManager.Instance.GetDefinition("XML"),
            _ => null
        };
    }

    // ---- Drag and drop files from Explorer (form-data and binary panels) ----
    // Preview events, because the text boxes inside the form list would otherwise
    // swallow the drag and refuse file drops.

    private void FilesDragOver(object sender, DragEventArgs e)
    {
        var accepts = AcceptsDrop(e);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(accepts ? sender : null);
        e.Handled = true;
    }

    private void FilesDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only clear the
        // highlight once the pointer has really left the panel.
        if (sender is FrameworkElement panel)
        {
            var point = e.GetPosition(panel);
            if (point.X < 0 || point.Y < 0 || point.X >= panel.ActualWidth || point.Y >= panel.ActualHeight)
                SetDropHighlight(null);
        }
        e.Handled = true;
    }

    private void FilesDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        if (AcceptsDrop(e) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _viewModel?.AddDroppedFiles(paths);
        e.Handled = true;
    }

    // Only real files (not Outlook attachments or other virtual items), only in the
    // modes that upload files, and not while a request is being sent.
    private bool AcceptsDrop(DragEventArgs e) =>
        _viewModel is { IsLoading: false, SelectedBodyMode: BodyMode.FormData or BodyMode.Binary } &&
        e.Data.GetDataPresent(DataFormats.FileDrop);

    private void SetDropHighlight(object? target)
    {
        var formActive = ReferenceEquals(target, FormPartsPanel);
        FormDropOutline.Stroke = (Brush)FindResource(formActive ? "AccentBlue" : "BorderBrush");
        FormDropOutline.Fill = formActive ? DropFill : Brushes.Transparent;
        BinaryDropOverlay.Visibility = ReferenceEquals(target, BinaryPanel) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly Brush DropFill = CreateDropFill();

    private static Brush CreateDropFill()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x1A, 0x58, 0xA6, 0xFF)); // AccentBlue at 10%
        brush.Freeze();
        return brush;
    }
}
