using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TransIt.Services;

public static class TextFitter
{
    public const double MinFontSize = 8.0;
    public const double MaxFontSize = 96.0;

    // Binary search for largest fontSize where wrapped text fits in width × height.
    public static double FitFontSize(string text, double availableWidth, double availableHeight)
    {
        double maxWidth = Math.Max(1, availableWidth);
        int lo = (int)MinFontSize;
        int hi = (int)MaxFontSize;

        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                mid,
                Brushes.Black,
                1.0)
            { MaxTextWidth = maxWidth };

            if (ft.Height <= availableHeight)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }
}
