using System.Windows;

namespace TransIt.Models;

public class OcrBlock
{
    public List<OcrLine> Lines { get; set; } = [];
    public string FullText => string.Join("\n", Lines.Select(l => l.FullText));

    public Rect BoundingRect
    {
        get
        {
            var r = Rect.Empty;
            foreach (var l in Lines) r.Union(l.BoundingRect);
            return r;
        }
    }

    /// Group OCR lines into paragraph blocks via single-pass pairwise clustering
    /// (union-find / connected components), instead of a sequential per-column scan.
    /// Two lines merge only if ALL hold:
    ///   - horizontally aligned (overlapping X ranges, or a small gutter) — keeps
    ///     side-by-side columns apart regardless of Y-sort order.
    ///   - similar height — different font size/section → different block.
    ///   - vertical gap small relative to BOTH the local line height and a globally
    ///     estimated line-pitch (median nearest-neighbor gap across the whole capture).
    ///     The global estimate is computed once up front from the data, not updated as
    ///     groups grow — a locally-evolving baseline drifts upward each time a slightly
    ///     loose gap is accepted, eventually swallowing a real paragraph break too.
    ///   - the earlier (topmost) of the two lines isn't much shorter than the later one —
    ///     a short line ending early signals a manual line break (Enter) or end of
    ///     paragraph, not a wrapped continuation. Direction matters: a short line
    ///     *starting* a pair (e.g. a title before a full-width paragraph) blocks the
    ///     merge; a short line ending a paragraph's last line does not, since there is no
    ///     later line in that pair to compare against.
    /// Connected components of the merge graph are the final paragraph blocks.
    public static List<OcrBlock> GroupLines(List<OcrLine> lines)
    {
        if (lines.Count == 0) return [];

        int n = lines.Count;
        double medianPitch = EstimateLinePitch(lines);

        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (ShouldMerge(lines[i].BoundingRect, lines[j].BoundingRect, medianPitch))
                    Union(i, j);
            }
        }

        return Enumerable.Range(0, n)
            .GroupBy(Find)
            .Select(g => new OcrBlock { Lines = g.Select(i => lines[i]).OrderBy(l => l.BoundingRect.Y).ToList() })
            .OrderBy(b => b.BoundingRect.Y)
            .ToList();
    }

    private static bool ShouldMerge(Rect aRect, Rect bRect, double medianPitch)
    {
        double localH = Math.Max(aRect.Height, bRect.Height);

        double horizontalOverlap = Math.Min(aRect.Right, bRect.Right) - Math.Max(aRect.Left, bRect.Left);
        double horizontalGap = -horizontalOverlap;
        bool horizontallyAligned = horizontalOverlap > 0 || horizontalGap <= localH * 0.5;
        if (!horizontallyAligned) return false;

        bool heightSimilar = Math.Max(aRect.Height, bRect.Height) / Math.Min(aRect.Height, bRect.Height) <= 1.25;
        if (!heightSimilar) return false;

        var top = aRect.Y <= bRect.Y ? aRect : bRect;
        var bottom = aRect.Y <= bRect.Y ? bRect : aRect;
        double gap = bottom.Y - (top.Y + top.Height);

        double gapThreshold = Math.Max(localH * 1.0, medianPitch * 1.3);
        if (gap > gapThreshold) return false;

        bool topLineTooShort = top.Width < bottom.Width * 0.6;
        return !topLineTooShort;
    }

    // Robust global line-pitch estimate: for each line, find its nearest horizontally-aligned
    // neighbor directly below it, collect those gaps, take the median. Used as a data-driven
    // baseline instead of a fixed multiple of line height, which can be off for documents with
    // looser-than-default line spacing.
    private static double EstimateLinePitch(List<OcrLine> lines)
    {
        var gaps = new List<double>();
        foreach (var line in lines)
        {
            var aRect = line.BoundingRect;
            double? nearest = null;
            foreach (var other in lines)
            {
                if (ReferenceEquals(other, line)) continue;
                var bRect = other.BoundingRect;
                if (bRect.Y <= aRect.Y) continue;

                double localH = Math.Max(aRect.Height, bRect.Height);
                double horizontalOverlap = Math.Min(aRect.Right, bRect.Right) - Math.Max(aRect.Left, bRect.Left);
                double horizontalGap = -horizontalOverlap;
                bool horizontallyAligned = horizontalOverlap > 0 || horizontalGap <= localH * 0.5;
                if (!horizontallyAligned) continue;

                double gap = bRect.Y - (aRect.Y + aRect.Height);
                if (nearest is null || gap < nearest) nearest = gap;
            }
            if (nearest is not null) gaps.Add(nearest.Value);
        }

        if (gaps.Count == 0) return 0;
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }
}
