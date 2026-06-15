using System.Windows;
using System.Windows.Interop;
using TransIt.Infrastructure;

namespace TransIt.Infrastructure;

internal static class DpiHelper
{
    public static double GetDpiScaleForWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return GetDpiScaleForHwnd(hwnd);
    }

    public static double GetDpiScaleForHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 1.0;
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    public static double GetPrimaryScreenDpiScale()
    {
        var source = PresentationSource.FromVisual(Application.Current.MainWindow
            ?? throw new InvalidOperationException("No main window"));
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    public static int LogicalToPhysical(double logical, double dpiScale) =>
        (int)Math.Round(logical * dpiScale);

    public static double PhysicalToLogical(double physical, double dpiScale) =>
        dpiScale > 0 ? physical / dpiScale : physical;
}
