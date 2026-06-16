using System.Drawing;
using PaddleOCRSharp;
using AppOcrLine = TransIt.Models.OcrLine;
using AppOcrWord = TransIt.Models.OcrWord;

namespace TransIt.Core;

public class OcrService : IDisposable
{
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private PaddleOCREngine? _engine;
    private bool _disposed;

    // languageTag kept for call-site compatibility; OCRModelConfig.V5_CN is a combined
    // Chinese+English model, so one shared engine covers both bundled source languages.
    public async Task<List<AppOcrLine>> RecognizeAsync(Bitmap bitmap, string languageTag,
                                                        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var engine = await GetEngineAsync(ct);
        ct.ThrowIfCancellationRequested();

        OCRResult result = await Task.Run(() => engine.DetectText(bitmap), ct);

        var rawBlocks = result.TextBlocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .Select(b => (Text: b.Text, Rect: BoundingRectOf(b.BoxPoints)))
            .ToList();

        return GroupIntoLines(rawBlocks);
    }

    public static IReadOnlyList<string> GetInstalledLanguageTags() => ["en", "zh"];

    private async Task<PaddleOCREngine> GetEngineAsync(CancellationToken ct)
    {
        if (_engine != null) return _engine;

        await _engineLock.WaitAsync(ct);
        try
        {
            _engine ??= new PaddleOCREngine(OCRModelConfig.V5_CN, new OCRParameter
            {
                cls = false,
                use_angle_cls = false,
            });
            return _engine;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private static System.Windows.Rect BoundingRectOf(IList<OCRPoint> points)
    {
        double minX = points.Min(p => p.X);
        double minY = points.Min(p => p.Y);
        double maxX = points.Max(p => p.X);
        double maxY = points.Max(p => p.Y);
        return new System.Windows.Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    // PaddleOCR's DetectText returns flat word/phrase-level boxes (not pre-grouped into
    // lines like Windows.Media.Ocr did), so rows must be clustered here before the
    // paragraph-level grouping in OcrBlock.GroupLines runs on top.
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
            // A row band can still contain disjoint columns (e.g. two side-by-side text
            // blocks at the same Y) — split on large horizontal gaps before treating the
            // rest as one line of words.
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
