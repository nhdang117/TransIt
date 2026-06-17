using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TransIt.Infrastructure;

namespace TransIt.Windows.Selection;

public partial class ScrollCaptureBar : Window
{
    public event EventHandler? FinalizeRequested;
    public event EventHandler? CancelRequested;

    public ScrollCaptureBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 16;
        Top  = screen.Bottom - Height - 16;
    }

    public void UpdateCount(int count, bool gapDetected = false, string unit = "line")
    {
        CountLabel.Text = $"{count} {unit}{(count == 1 ? "" : "s")} captured";
        SummarizeButton.IsEnabled = count > 0;
        GapWarning.Visibility = gapDetected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSummarize(object sender, RoutedEventArgs e)
    {
        FinalizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
