using System.Drawing;
using System.Drawing.Imaging;

namespace TransIt.Services;

/// Perceptual hash (average hash) for detecting screen changes.
public class ChangeDetector
{
    private ulong _lastHash;

    public bool HasChanged(Bitmap bmp, int threshold = 5)
    {
        ulong hash = ComputeHash(bmp);
        bool changed = HammingDistance(_lastHash, hash) >= threshold;
        _lastHash = hash;
        return changed;
    }

    public void Reset() => _lastHash = 0;

    private static ulong ComputeHash(Bitmap bmp)
    {
        using var small = new Bitmap(bmp, new Size(16, 16));
        using var gray = ToGrayscale(small);

        long total = 0;
        int[] pixels = new int[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                pixels[y * 16 + x] = gray.GetPixel(x, y).R;
                total += pixels[y * 16 + x];
            }

        long avg = total / 256;
        ulong hash = 0;
        for (int i = 0; i < 64; i++)
            if (pixels[i] >= avg) hash |= 1UL << i;

        return hash;
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        ulong x = a ^ b;
        int count = 0;
        while (x != 0) { count += (int)(x & 1); x >>= 1; }
        return count;
    }

    private static Bitmap ToGrayscale(Bitmap src)
    {
        var result = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        // Simple grayscale via ColorMatrix
        var cm = new System.Drawing.Imaging.ColorMatrix(new float[][]
        {
            [0.299f, 0.299f, 0.299f, 0, 0],
            [0.587f, 0.587f, 0.587f, 0, 0],
            [0.114f, 0.114f, 0.114f, 0, 0],
            [0,      0,      0,      1, 0],
            [0,      0,      0,      0, 1]
        });
        var ia = new System.Drawing.Imaging.ImageAttributes();
        ia.SetColorMatrix(cm);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return result;
    }
}
