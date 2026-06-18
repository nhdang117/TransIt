using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TransIt.Infrastructure;

namespace TransIt.Windows.Selection;

public partial class ScrollPreviewWindow : Window
{
    private bool _closed;

    public ScrollPreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        // WS_EX_NOACTIVATE: don't steal focus from target app when shown.
        // WS_EX_TRANSPARENT: pass all mouse hit-tests through to the target app beneath,
        // so "Scroll inactive windows" routes wheel events to target, not this preview.
        // Both must be set before Show() — SourceInitialized fires before ShowWindow.
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT);
    }

    // Call after Show() and after bar.PositionAboveRect() so barTop is already set.
    // Centers preview just above the bar, aligned to the same physRect center.
    public void PositionAboveRect(System.Drawing.Rectangle physRect, double dpiScale, double barTop)
    {
        double logX = physRect.X / dpiScale;
        double logW = physRect.Width / dpiScale;

        var wa = SystemParameters.WorkArea;
        Left = Math.Clamp(logX + (logW - Width) / 2, wa.Left, wa.Right - Width);
        Top  = Math.Clamp(barTop - Height - 4,        wa.Top,  wa.Bottom - Height);
    }

    // Called from capture thread with the latest stitched JPEG after each new frame.
    public void UpdateStitched(byte[] stitchedJpeg, int frameCount)
    {
        if (_closed) return;

        var src = JpegToFrozenBitmapSource(stitchedJpeg);

        Dispatcher.Invoke(() =>
        {
            if (_closed) return;
            PreviewImage.Source = src;
            CountLabel.Text = $"{frameCount} image{(frameCount == 1 ? "" : "s")}";
            Scroller.UpdateLayout();
            Scroller.ScrollToBottom();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private static BitmapSource JpegToFrozenBitmapSource(byte[] jpeg)
    {
        using var ms = new MemoryStream(jpeg);
        var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var src = decoder.Frames[0];
        if (!src.IsFrozen) src.Freeze();
        return src;
    }
}
