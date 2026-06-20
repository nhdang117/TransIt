using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using RapidOcrNet;
using SkiaSharp;
using AppOcrLine = TransIt.Models.OcrLine;
using AppOcrWord = TransIt.Models.OcrWord;

namespace TransIt.Core;

public partial class OcrService : IDisposable
{
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private RapidOcr? _engine;
    private int _callCount;
    private bool _disposed;

    private static readonly RapidOcrOptions _options = RapidOcrOptions.Default with { DoAngle = false };

    private static string ModelPath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "models", "v5", file);

    public async Task<List<AppOcrLine>> RecognizeAsync(Bitmap bitmap, string languageTag,
                                                        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();

        var engine = await GetEngineAsync(ct);
        ct.ThrowIfCancellationRequested();

        int call = Interlocked.Increment(ref _callCount);
        sw.Restart();

        using var skBitmap = ToSkBitmap(bitmap);
        var result = await Task.Run(() => engine.Detect(skBitmap, _options), ct);
        Debug.WriteLine($"[OCR] detect_text  = {sw.ElapsedMilliseconds} ms  call=#{call} ({bitmap.Width}x{bitmap.Height} px, {result.TextBlocks?.Length ?? 0} blocks)");

        sw.Restart();
        var rawBlocks = (result.TextBlocks ?? [])
            .Select(b => (Text: CleanOcrText(b.Text), Rect: BoundingRectOf(b.BoxPoints)))
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .ToList();

        // If capture is primarily non-CJK (Latin/etc.), pure-CJK blocks are OCR noise —
        // drop them before grouping so they don't get merged into Latin paragraphs.
        int totalLetters = rawBlocks.Sum(b => b.Text.Count(char.IsLetter));
        int cjkLetters   = rawBlocks.Sum(b => b.Text.Count(IsCjkIdeograph));
        bool documentIsPrimarilyNonCjk = totalLetters > 0 && (double)cjkLetters / totalLetters < 0.5;
        if (documentIsPrimarilyNonCjk)
            rawBlocks = [.. rawBlocks.Where(b => !b.Text.All(c => IsCjkIdeograph(c) || !char.IsLetter(c)))];

        var lines = GroupIntoLines(rawBlocks);
        Debug.WriteLine($"[OCR] group_lines  = {sw.ElapsedMilliseconds} ms  ({rawBlocks.Count} blocks → {lines.Count} lines)");

        return lines;
    }

    public static IReadOnlyList<string> GetInstalledLanguageTags() => ["en", "zh"];

    public async Task WarmUpAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await GetEngineAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCR] warmup failed: {ex.Message}");
        }
    }

    private async Task<RapidOcr> GetEngineAsync(CancellationToken ct)
    {
        if (_engine != null) return _engine;

        await _engineLock.WaitAsync(ct);
        try
        {
            if (_engine == null)
            {
                var sw = Stopwatch.StartNew();
                var engine = new RapidOcr();
                engine.InitModels(
                    detPath:  ModelPath("ch_PP-OCRv5_mobile_det.onnx"),
                    clsPath:  ModelPath("ch_ppocr_mobile_v2.0_cls_infer.onnx"),
                    recPath:  ModelPath("ch_PP-OCRv5_rec_mobile.onnx"),
                    keysPath: ModelPath("ppocrv5_dict.txt"));
                Debug.WriteLine($"[OCR] engine_new   = {sw.ElapsedMilliseconds} ms  model=RapidOCR-PP-OCRv5-CN");

                sw.Restart();
                using var warmup = new SKBitmap(960, 640);
                await Task.Run(() => engine.Detect(warmup, _options), ct);
                Debug.WriteLine($"[OCR] warmup       = {sw.ElapsedMilliseconds} ms");

                _engine = engine;
            }
            return _engine;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private static unsafe SKBitmap ToSkBitmap(Bitmap src)
    {
        var info = new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var dst = new SKBitmap(info);
        var bmpData = src.LockBits(
            new Rectangle(0, 0, src.Width, src.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            byte* src0 = (byte*)bmpData.Scan0;
            byte* dst0 = (byte*)dst.GetPixels();
            int rowBytes = info.RowBytes;
            int stride   = Math.Abs(bmpData.Stride);
            for (int y = 0; y < src.Height; y++)
                Buffer.MemoryCopy(src0 + y * stride, dst0 + y * rowBytes, rowBytes, rowBytes);
        }
        finally
        {
            src.UnlockBits(bmpData);
        }
        return dst;
    }

    private static System.Windows.Rect BoundingRectOf(SKPointI[] points)
    {
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new System.Windows.Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    // internal so TranslationService can apply it again on grouped OcrBlock.FullText
    internal static string CleanOcrText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Normalize chars; keep \n as segment separator for per-line processing below.
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            var cat = char.GetUnicodeCategory(c);
            if (c == '\r' || c == '\n')
                sb.Append('\n');
            else if (cat == System.Globalization.UnicodeCategory.Control)
                sb.Append(' ');
            else if (cat is System.Globalization.UnicodeCategory.PrivateUse
                          or System.Globalization.UnicodeCategory.Surrogate
                          or System.Globalization.UnicodeCategory.Format
                          or System.Globalization.UnicodeCategory.OtherNotAssigned)
            { /* drop */ }
            else
                sb.Append(c);
        }

        // Process each segment independently so a pure-CJK OCR artifact line that was
        // later merged with a Latin line (by OcrBlock.GroupLines) is stripped on its own
        // rather than only when the whole joined string has mixed script.
        var segments = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            bool hasCjk = seg.Any(IsCjkIdeograph);
            bool hasNonCjkLetter = seg.Any(c => char.IsLetter(c) && !IsCjkIdeograph(c));
            string s = (hasCjk && hasNonCjkLetter)
                ? string.Concat(seg.Where(c => !IsCjkIdeograph(c)))
                : seg;

            // Strip artifact chars at line start: brackets, backslash, stray ellipsis/middle-dot
            s = s.TrimStart(']', '[', '}', '{', '|', '\\', '~', '`', '^',
                            '…', '‥', '·', '・');
            s = WhitespaceRun.Replace(s, " ").Trim();
            if (s.Length > 0) parts.Add(s);
        }

        return string.Join(" ", parts);
    }

    private static readonly System.Text.RegularExpressions.Regex WhitespaceRun =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    // CJK Unified Ideographs + Extension A + Compatibility Ideographs
    private static bool IsCjkIdeograph(char c) =>
        (c >= '一' && c <= '鿿') ||
        (c >= '㐀' && c <= '䶿') ||
        (c >= '豈' && c <= '﫿');

    private static List<AppOcrLine> GroupIntoLines(List<(string Text, System.Windows.Rect Rect)> rawBlocks)
    {
        if (rawBlocks.Count == 0) return [];

        var sorted = rawBlocks.OrderBy(b => b.Rect.Y).ThenBy(b => b.Rect.X).ToList();
        var rows = new List<List<(string Text, System.Windows.Rect Rect)>> { new() { sorted[0] } };

        for (int i = 1; i < sorted.Count; i++)
        {
            var block = sorted[i];
            var row = rows[^1];
            double rowCenterY = row.Average(b => b.Rect.Y + b.Rect.Height / 2);
            double rowHeight = row.Max(b => b.Rect.Height);
            double blockCenterY = block.Rect.Y + block.Rect.Height / 2;

            if (Math.Abs(blockCenterY - rowCenterY) <= rowHeight * 0.5)
                row.Add(block);
            else
                rows.Add(new List<(string Text, System.Windows.Rect Rect)> { block });
        }

        var lines = new List<AppOcrLine>();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(b => b.Rect.X).ToList();
            double rowHeight = ordered.Max(b => b.Rect.Height);
            var segments = new List<List<(string Text, System.Windows.Rect Rect)>> { new() { ordered[0] } };

            for (int i = 1; i < ordered.Count; i++)
            {
                var prevRect = segments[^1][^1].Rect;
                double horizontalGap = ordered[i].Rect.X - prevRect.Right;
                if (horizontalGap > rowHeight * 2.0)
                    segments.Add(new List<(string Text, System.Windows.Rect Rect)> { ordered[i] });
                else
                    segments[^1].Add(ordered[i]);
            }

            foreach (var segment in segments)
            {
                var words = segment.Select(b => new AppOcrWord { Text = b.Text, BoundingRect = b.Rect }).ToList();

                var union = System.Windows.Rect.Empty;
                foreach (var w in words) union.Union(w.BoundingRect);

                lines.Add(new AppOcrLine
                {
                    Words = words,
                    FullText = string.Join(" ", words.Select(w => w.Text)),
                    BoundingRect = union,
                });
            }
        }
        return lines;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine?.Dispose();
        _engineLock.Dispose();
    }
}
