using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using AppOcrLine = TransIt.Models.OcrLine;
using AppOcrWord = TransIt.Models.OcrWord;

namespace TransIt.Core;

public class OcrService
{
    public async Task<List<AppOcrLine>> RecognizeAsync(Bitmap bitmap, string languageTag,
                                                        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var language = new Language(languageTag);
        var engine   = OcrEngine.TryCreateFromLanguage(language)
                       ?? OcrEngine.TryCreateFromUserProfileLanguages()
                       ?? throw new InvalidOperationException(
                           $"No OCR engine available for language '{languageTag}'.");

        using var swBitmap = ConvertToSoftwareBitmap(bitmap);
        ct.ThrowIfCancellationRequested();

        var result = await engine.RecognizeAsync(swBitmap);

        var lines = new List<AppOcrLine>();
        foreach (var ocrLine in result.Lines)
        {
            var words = ocrLine.Words
                .Select(w => new AppOcrWord
                {
                    Text = w.BoundingRect.Width > 0 ? w.Text : string.Empty,
                    BoundingRect = new System.Windows.Rect(
                        w.BoundingRect.X, w.BoundingRect.Y,
                        w.BoundingRect.Width, w.BoundingRect.Height)
                })
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .ToList();

            if (words.Count == 0) continue;

            lines.Add(new AppOcrLine
            {
                Words = words,
                FullText = string.Join(" ", words.Select(w => w.Text)),
                BoundingRect = UnionRects(words.Select(w => w.BoundingRect))
            });
        }
        return lines;
    }

    public static IReadOnlyList<string> GetInstalledLanguageTags() =>
        OcrEngine.AvailableRecognizerLanguages
                 .Select(l => l.LanguageTag)
                 .ToList();

    private static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap gdiBitmap)
    {
        using var bmpBgra = gdiBitmap.Clone(
            new Rectangle(0, 0, gdiBitmap.Width, gdiBitmap.Height),
            PixelFormat.Format32bppArgb);

        var bmpData = bmpBgra.LockBits(
            new Rectangle(0, 0, bmpBgra.Width, bmpBgra.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            int byteCount = Math.Abs(bmpData.Stride) * bmpBgra.Height;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);

            var swBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                bmpBgra.Width,
                bmpBgra.Height,
                BitmapAlphaMode.Premultiplied);

            swBitmap.CopyFromBuffer(pixels.AsBuffer());
            return swBitmap;
        }
        finally
        {
            bmpBgra.UnlockBits(bmpData);
        }
    }

    private static System.Windows.Rect UnionRects(IEnumerable<System.Windows.Rect> rects)
    {
        var result = System.Windows.Rect.Empty;
        foreach (var r in rects)
            result.Union(r);
        return result;
    }
}
