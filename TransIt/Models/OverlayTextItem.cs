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
        Bitmap sourceBitmap)
    {
        var logicalRect = block.BoundingRect;

        // Bitmap is forced to 96 DPI (1 pixel = 1 WPF DIP), so sample at DIP coords directly.
        var sampleRect = new Rectangle(
            (int)logicalRect.X,
            (int)logicalRect.Y,
            (int)logicalRect.Width,
            (int)logicalRect.Height);

        sampleRect.Intersect(new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height));
        if (sampleRect.Width < 1) sampleRect.Width = 1;
        if (sampleRect.Height < 1) sampleRect.Height = 1;

        var bgColor = Services.ColorSampler.SampleBackground(sourceBitmap, sampleRect);
        var fgColor = Services.ColorSampler.SampleForeground(sourceBitmap, sampleRect, bgColor);

        // Use per-line height so multi-line blocks start at a sensible font size.
        double lineHeight = logicalRect.Height / Math.Max(1, block.Lines.Count);
        double fontSize   = Services.FontSizeEstimator.Estimate(lineHeight);

        return new OverlayTextItem
        {
            TranslatedText = translatedText,
            ScreenRect     = logicalRect,
            FontSize       = fontSize,
            BackgroundColor = ToMediaColor(bgColor),
            ForegroundColor = ToMediaColor(fgColor)
        };
    }

    private static System.Windows.Media.Color ToMediaColor(Color c) =>
        System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
}
