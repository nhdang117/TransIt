using System.Drawing;
using TransIt.Infrastructure;

namespace TransIt.Core;

public static class ScreenCaptureService
{
    /// Captures the monitor the cursor is on. Returns bitmap in physical pixels, DPI scale, and monitor physical rect.
    public static (Bitmap bitmap, double dpiScale, System.Drawing.Rectangle monRect) CaptureMonitorAtCursor()
    {
        var (monRect, dpiScale) = DpiHelper.GetMonitorAtCursor();
        return (CaptureRegion(monRect), dpiScale, monRect);
    }

    public static Bitmap CaptureRegion(Rectangle physicalRegion)
    {
        var bitmap = new Bitmap(physicalRegion.Width, physicalRegion.Height,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(physicalRegion.X, physicalRegion.Y, 0, 0,
                         physicalRegion.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }
}
