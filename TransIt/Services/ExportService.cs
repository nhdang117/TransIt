using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TransIt.Services;

public static class ExportService
{
    public static void SaveToPng(UIElement element, string filePath)
    {
        var size = new Size(element.RenderSize.Width, element.RenderSize.Height);
        element.Measure(size);
        element.Arrange(new Rect(size));

        var rtb = new RenderTargetBitmap(
            (int)element.RenderSize.Width,
            (int)element.RenderSize.Height,
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);

        rtb.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var fs = new FileStream(filePath, FileMode.Create);
        encoder.Save(fs);
    }

    public static void CopyToClipboard(UIElement element)
    {
        var rtb = new RenderTargetBitmap(
            (int)element.RenderSize.Width,
            (int)element.RenderSize.Height,
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);

        rtb.Render(element);
        Clipboard.SetImage(rtb);
    }
}
