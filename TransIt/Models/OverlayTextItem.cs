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
        // OCR returns coordinates in sourceBitmap pixel space (physical pixels).
        // Use them directly to sample colours from the bitmap.
        var physRect = block.BoundingRect;
        var sampleRect = new Rectangle(
            (int)physRect.X,
            (int)physRect.Y,
            Math.Max(1, (int)physRect.Width),
            Math.Max(1, (int)physRect.Height));

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

        // Average individual OCR line heights (physical px → DIPs).
        // Do NOT use block.BoundingRect.Height / lineCount — the union bounding
        // rect includes inter-line gaps, which vary per app (code vs prose) and
        // inflate lineHeight inconsistently.
        double avgLinePhys = block.Lines.Average(l => l.BoundingRect.Height);
        double lineHeight  = avgLinePhys / dpiScale;
        double fontSize    = Services.FontSizeEstimator.Estimate(lineHeight, block.FullText);

        // Translated text is often longer than the source; allow generous vertical growth
        // (the render border has no fixed height and auto-grows) before shrinking the font,
        // so only pathological overflow (e.g. a single short word translating to a long phrase) shrinks.
        double availableHeight = Math.Max(logicalRect.Height, lineHeight * block.Lines.Count) * 2.5;
        fontSize = Services.TextFitter.FitFontSize(translatedText, fontSize, logicalRect.Width, availableHeight);

        // DEBUG — writes sizing data to %APPDATA%\TransIt\overlay_debug.log
        // Remove once font-size tuning is confirmed.
        try
        {
            var dbgPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TransIt", "overlay_debug.log");
            System.IO.File.AppendAllText(dbgPath,
                $"dpi={dpiScale:F2} physH={physRect.Height:F0}px lines={block.Lines.Count} " +
                $"logH={logicalRect.Height:F1} lineH={lineHeight:F1} fontSize={fontSize:F1}\n");
        }
        catch { }

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
