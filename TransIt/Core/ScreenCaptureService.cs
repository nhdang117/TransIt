using System.Drawing;
using TransIt.Infrastructure;

namespace TransIt.Core;

public static class ScreenCaptureService
{
    public static Bitmap CaptureFullScreen()
    {
        // GetSystemMetrics returns physical pixel dimensions for a DPI-aware process,
        // unlike SystemParameters which returns logical DIPs. Using physical coords
        // here ensures CopyFromScreen (which also operates in physical pixels) captures
        // the entire virtual screen at full resolution regardless of DPI scale setting.
        int left   = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int top    = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int width  = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        return bitmap;
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
