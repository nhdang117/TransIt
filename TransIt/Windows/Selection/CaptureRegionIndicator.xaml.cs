using System.Windows;
using System.Windows.Interop;
using TransIt.Infrastructure;

namespace TransIt.Windows.Selection;

public partial class CaptureRegionIndicator : Window
{
    public CaptureRegionIndicator()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Exclude from GDI/DWM screen capture so frames don't include the border overlay.
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        // Also make click-through so user can scroll the content underneath.
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_TRANSPARENT);
    }

    public void ShowForRect(System.Drawing.Rectangle physRect, double dpiScale)
    {
        Left   = physRect.Left   / dpiScale;
        Top    = physRect.Top    / dpiScale;
        Width  = physRect.Width  / dpiScale;
        Height = physRect.Height / dpiScale;
        Show();
    }
}
