using System.Drawing;
using System.Windows;

namespace TransIt.Core;

public static class ScreenCaptureService
{
    public static Bitmap CaptureFullScreen()
    {
        var vscreen = new Rectangle(
            (int)SystemParameters.VirtualScreenLeft,
            (int)SystemParameters.VirtualScreenTop,
            (int)SystemParameters.VirtualScreenWidth,
            (int)SystemParameters.VirtualScreenHeight);

        // Use physical pixels for the bitmap
        var bitmap = new Bitmap(vscreen.Width, vscreen.Height,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(vscreen.X, vscreen.Y, 0, 0, vscreen.Size,
                         CopyPixelOperation.SourceCopy);
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
