using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TransIt.Services;

public static class ColorSampler
{
    /// Sample the dominant background color by looking at a border strip around the rect.
    public static Color SampleBackground(Bitmap bmp, Rectangle rect)
    {
        var samples = new List<Color>();

        // Top and bottom rows
        int sampleStep = Math.Max(1, rect.Width / 20);
        for (int x = rect.Left; x < rect.Right; x += sampleStep)
        {
            if (rect.Top >= 0 && rect.Top < bmp.Height && x < bmp.Width)
                samples.Add(bmp.GetPixel(x, rect.Top));
            int bottom = rect.Bottom - 1;
            if (bottom >= 0 && bottom < bmp.Height && x < bmp.Width)
                samples.Add(bmp.GetPixel(x, bottom));
        }

        // Left and right columns
        sampleStep = Math.Max(1, rect.Height / 20);
        for (int y = rect.Top; y < rect.Bottom; y += sampleStep)
        {
            if (rect.Left >= 0 && rect.Left < bmp.Width && y < bmp.Height)
                samples.Add(bmp.GetPixel(rect.Left, y));
            int right = rect.Right - 1;
            if (right >= 0 && right < bmp.Width && y < bmp.Height)
                samples.Add(bmp.GetPixel(right, y));
        }

        if (samples.Count == 0) return Color.White;
        return AverageColor(samples);
    }

    /// Detect foreground color by finding pixels most different from background.
    public static Color SampleForeground(Bitmap bmp, Rectangle rect, Color bg)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return Color.Black;

        var candidates = new List<Color>();
        int step = Math.Max(1, Math.Min(rect.Width, rect.Height) / 10);

        for (int y = rect.Top; y < rect.Bottom && y < bmp.Height; y += step)
        {
            for (int x = rect.Left; x < rect.Right && x < bmp.Width; x += step)
            {
                var c = bmp.GetPixel(x, y);
                if (ColorDistance(c, bg) > 80) // meaningfully different from background
                    candidates.Add(c);
            }
        }

        if (candidates.Count == 0)
        {
            // Contrast fallback
            double bgLum = Luminance(bg);
            return bgLum > 0.5 ? Color.Black : Color.White;
        }

        return AverageColor(candidates);
    }

    private static Color AverageColor(List<Color> colors)
    {
        int r = 0, g = 0, b = 0;
        foreach (var c in colors) { r += c.R; g += c.G; b += c.B; }
        int n = colors.Count;
        return Color.FromArgb(r / n, g / n, b / n);
    }

    private static double ColorDistance(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static double Luminance(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
