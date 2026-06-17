using System.Drawing;
using System.Windows;

namespace TransIt.Models;

public class OverlayTextItem
{
    public string TranslatedText { get; set; } = string.Empty;
    /// Position in WPF logical pixels (DIPs) on the virtual screen.
    public Rect ScreenRect { get; set; }
    public double FontSize { get; set; } = 12;
    public System.Windows.Media.Color ForegroundColor { get; set; } = System.Windows.Media.Colors.Black;
    public System.Windows.Media.Color BackgroundColor { get; set; } = System.Windows.Media.Colors.White;
    public bool IsRightToLeft { get; set; }

    public static OverlayTextItem Build(
        OcrBlock block,
        string translatedText,
        Bitmap sourceBitmap,
        double dpiScale = 1.0)
    {
        translatedText = translatedText.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

        // OCR returns coordinates in sourceBitmap pixel space (physical pixels).
        // Use them directly to sample colours from the bitmap.
        var physRect = block.BoundingRect;
        var sampleRect = new Rectangle(
            (int)physRect.X,
            (int)physRect.Y,
            Math.Max(1, (int)physRect.Width),
            Math.Max(1, (int)physRect.Height));
        Console.WriteLine($"translatedText = '{translatedText}'");
        sampleRect.Intersect(new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height));
        if (sampleRect.Width < 1) sampleRect.Width = 1;
        if (sampleRect.Height < 1) sampleRect.Height = 1;

        var bgColor = Services.ColorSampler.SampleBackground(sourceBitmap, sampleRect);
        var fgColor = Services.ColorSampler.SampleForeground(sourceBitmap, sampleRect, bgColor);

        // Convert physical pixel rect to logical DIPs for WPF canvas placement.
        var logicalRect = dpiScale > 1.0
            ? new Rect(physRect.X / dpiScale, physRect.Y / dpiScale,
                       physRect.Width / dpiScale, physRect.Height / dpiScale)
            : physRect;

        double fontSize = Services.TextFitter.FitFontSize(translatedText, logicalRect.Width, logicalRect.Height);

return new OverlayTextItem
        {
            TranslatedText  = translatedText,
            ScreenRect      = logicalRect,
            FontSize        = fontSize,
            BackgroundColor = ToMediaColor(bgColor),
            ForegroundColor = ToMediaColor(fgColor)
        };
    }

    private static System.Windows.Media.Color ToMediaColor(Color c) =>
        System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
}
