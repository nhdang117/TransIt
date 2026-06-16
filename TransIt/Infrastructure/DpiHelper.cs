using System.Runtime.InteropServices;
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

    /// Returns the effective DPI scale of the primary monitor.
    public static double GetPrimaryDpiScale()
    {
        var pt = new NativeMethods.POINT { X = 0, Y = 0 };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        int hr = NativeMethods.GetDpiForMonitor(hMonitor, 0, out uint dpiX, out _);
        return hr == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
    }

    /// Physical rect + DPI scale of the monitor the cursor is currently on.
    public static (System.Drawing.Rectangle physRect, double dpiScale) GetMonitorAtCursor()
    {
        NativeMethods.GetCursorPos(out var pt);
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        return GetMonitorForHandle(hMonitor);
    }

    /// Physical rect + DPI scale of the monitor containing the given physical point.
    public static (System.Drawing.Rectangle physRect, double dpiScale) GetMonitorAtPoint(int x, int y)
    {
        var pt = new NativeMethods.POINT { X = x, Y = y };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        return GetMonitorForHandle(hMonitor);
    }

    private static (System.Drawing.Rectangle physRect, double dpiScale) GetMonitorForHandle(IntPtr hMonitor)
    {
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        NativeMethods.GetMonitorInfo(hMonitor, ref info);
        var rect = info.rcMonitor.ToRectangle();

        int hr = NativeMethods.GetDpiForMonitor(hMonitor, 0, out uint dpiX, out _);
        double scale = hr == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        return (rect, scale);
    }

    public static int LogicalToPhysical(double logical, double dpiScale) =>
        (int)Math.Round(logical * dpiScale);

    public static double PhysicalToLogical(double physical, double dpiScale) =>
        dpiScale > 0 ? physical / dpiScale : physical;
}
