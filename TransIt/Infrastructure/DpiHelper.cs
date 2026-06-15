using System.Windows;
using System.Windows.Interop;

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

    /// Returns the effective DPI scale of the primary monitor (1.0, 1.25, 1.5, 2.0 …).
    /// Uses GetDpiForMonitor — works at any Windows display-scale setting without
    /// requiring a visible window or relying on how GetSystemMetrics is virtualized
    /// for the current process DPI-awareness mode.
    public static double GetPrimaryDpiScale()
    {
        var pt = new NativeMethods.POINT { X = 0, Y = 0 };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        int hr = NativeMethods.GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
        return hr == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
    }

    public static int LogicalToPhysical(double logical, double dpiScale) =>
        (int)Math.Round(logical * dpiScale);

    public static double PhysicalToLogical(double physical, double dpiScale) =>
        dpiScale > 0 ? physical / dpiScale : physical;
}
