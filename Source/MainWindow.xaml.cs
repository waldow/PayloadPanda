using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace PayloadPanda;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.MainViewModel;

        // Set up AvalonEdit syntax highlighting
        var jsonHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
        RequestBodyEditor.SyntaxHighlighting = jsonHighlighting;
        ResponsePrettyEditor.SyntaxHighlighting = jsonHighlighting;

        // Bind AvalonEdit text (it doesn't support standard WPF binding)
        RequestBodyEditor.TextChanged += (s, e) =>
        {
            App.MainViewModel.RequestBody = RequestBodyEditor.Text;
        };

        App.MainViewModel.PropertyChanged += ViewModel_PropertyChanged;
        StateChanged += MainWindow_StateChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.RequestBody))
        {
            if (RequestBodyEditor.Text != App.MainViewModel.RequestBody)
                RequestBodyEditor.Text = App.MainViewModel.RequestBody;
        }
        else if (e.PropertyName == nameof(ViewModels.MainViewModel.ResponseBody))
        {
            ResponsePrettyEditor.Text = App.MainViewModel.ResponseBody;
        }
        else if (e.PropertyName == nameof(ViewModels.MainViewModel.EditorFontSize))
        {
            RequestBodyEditor.FontSize = App.MainViewModel.EditorFontSize;
            ResponsePrettyEditor.FontSize = App.MainViewModel.EditorFontSize;
        }
        else if (e.PropertyName == nameof(ViewModels.MainViewModel.EditorWordWrap))
        {
            RequestBodyEditor.WordWrap = App.MainViewModel.EditorWordWrap;
            ResponsePrettyEditor.WordWrap = App.MainViewModel.EditorWordWrap;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_StateChanged(object? sender, System.EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowBorder.BorderThickness = new Thickness(0);
            WindowBorder.Padding = new Thickness(0);
            MaximizeRestoreIcon.Data = Geometry.Parse("M 0 2 H 8 V 10 H 0 Z M 2 2 V 0 H 10 V 8 H 8");
            MaximizeRestoreButton.ToolTip = "Restore Down";
        }
        else
        {
            WindowBorder.BorderThickness = new Thickness(1);
            WindowBorder.Padding = new Thickness(0);
            MaximizeRestoreIcon.Data = Geometry.Parse("M 0 0 H 10 V 10 H 0 Z");
            MaximizeRestoreButton.ToolTip = "Maximize";
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            GetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void GetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, 0x00000002); // MONITOR_DEFAULTTONEAREST
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            var work = mi.rcWork;
            var mon = mi.rcMonitor;
            mmi.ptMaxPosition.X = Math.Abs(work.Left - mon.Left);
            mmi.ptMaxPosition.Y = Math.Abs(work.Top - mon.Top);
            mmi.ptMaxSize.X = Math.Abs(work.Right - work.Left);
            mmi.ptMaxSize.Y = Math.Abs(work.Bottom - work.Top);
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
