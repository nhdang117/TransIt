using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TransIt.Infrastructure;

namespace TransIt.Windows.Selection;

public partial class ScrollCaptureBar : Window
{
    public event EventHandler? FinalizeRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? StepRequested;
    public event EventHandler? AutoScrollRequested;

    public ScrollCaptureBar()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        // Must be set before Show() (SourceInitialized fires before ShowWindow) so the bar
        // never becomes foreground — target window stays focused for SendInput WHEEL routing.
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_NOACTIVATE);
    }

    // Call after Show(). Centers bar just above the bottom edge of physRect.
    public void PositionAboveRect(System.Drawing.Rectangle physRect, double dpiScale)
    {
        double logX      = physRect.X / dpiScale;
        double logW      = physRect.Width / dpiScale;
        double logBottom = physRect.Bottom / dpiScale;

        var wa = SystemParameters.WorkArea;
        Left = Math.Clamp(logX + (logW - Width) / 2, wa.Left, wa.Right - Width);
        Top  = Math.Clamp(logBottom - Height - 8,    wa.Top,  wa.Bottom - Height);
    }

    public void UpdateCount(int count, bool gapDetected = false, string unit = "image")
    {
        CountLabel.Text = $"{count} {unit}{(count == 1 ? "" : "s")} captured";
        SummarizeButton.IsEnabled = count > 0;
        GapWarning.Visibility = gapDetected ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetStatus(string text) => StatusLabel.Text = text;

    private void OnStep(object sender, RoutedEventArgs e)        => StepRequested?.Invoke(this, EventArgs.Empty);
    private void OnAutoScroll(object sender, RoutedEventArgs e)  => AutoScrollRequested?.Invoke(this, EventArgs.Empty);
    private void OnSummarize(object sender, RoutedEventArgs e)   => FinalizeRequested?.Invoke(this, EventArgs.Empty);
    private void OnCancel(object sender, RoutedEventArgs e)      => CancelRequested?.Invoke(this, EventArgs.Empty);
}
