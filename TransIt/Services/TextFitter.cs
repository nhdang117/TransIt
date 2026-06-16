using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TransIt.Services;

public static class TextFitter
{
    public const double MinFontSize = 8.0;
    private const double ShrinkStep = 1.0;

    // Shrinks fontSize until the wrapped text fits within availableHeight (with tolerance),
    // or until MinFontSize is reached. Translated text is often longer than the source OCR
    // line, so a modest overflow is expected and tolerated before shrinking kicks in.
    public static double FitFontSize(
        string text, double initialFontSize, double availableWidth, double availableHeight,
        double toleranceFactor = 1.1)
    {
        double fontSize = initialFontSize;
        double maxWidth = Math.Max(1, availableWidth);

        while (fontSize > MinFontSize)
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                fontSize,
                Brushes.Black,
                1.0)
            {
                MaxTextWidth = maxWidth,
            };

            if (formatted.Height <= availableHeight * toleranceFactor) break;
            fontSize -= ShrinkStep;
        }

        return Math.Max(fontSize, MinFontSize);
    }
}
