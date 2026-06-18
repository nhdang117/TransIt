using System.Windows;
using System.Windows.Interop;
using TransIt.Infrastructure;

namespace TransIt.Windows.Selection;

public partial class CaptureRegionIndicator : Window
{
    public CaptureRegionIndicator()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        // WS_EX_TRANSPARENT: click/wheel pass through to target app beneath.
        // WS_EX_NOACTIVATE: don't steal focus from target when shown.
        // Both set before Show() via SourceInitialized.
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE);
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
