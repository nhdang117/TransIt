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
                if (!ShouldMerge(lines[i].BoundingRect, lines[j].BoundingRect, medianPitch)) continue;
                var lower = lines[i].BoundingRect.Y <= lines[j].BoundingRect.Y ? lines[j] : lines[i];
                var upper = lines[i].BoundingRect.Y <= lines[j].BoundingRect.Y ? lines[i] : lines[j];
                if (IsNewParagraphStart(lower.FullText, upper.FullText)) continue;
                // Prevent A+C merging over fence line B between them: block {A,C} would
                // wrap around block {B} in Y-space, causing visible overlay overlap.
                if (HasAlignedFenceBetween(upper, lower, lines)) continue;
                Union(i, j);
            }
        }

        return Enumerable.Range(0, n)
            .GroupBy(Find)
            .Select(g => new OcrBlock { Lines = g.Select(i => lines[i]).OrderBy(l => l.BoundingRect.Y).ToList() })
            .OrderBy(b => b.BoundingRect.Y)
            .ToList();
    }

    /// Top-down grouping: assign each line to the layout region that covers the majority
    /// of the LINE's own area (not the region's area — regions are paragraph/column-sized,
    /// lines are tiny, so containment must be measured relative to the small box). Lines
    /// the layout pass missed or only partially covered fall back to the bottom-up
    /// GroupLines heuristic, so a bad/absent region never drops text from the output.
    public static List<OcrBlock> GroupLinesWithLayout(List<OcrLine> lines, List<LayoutRegion> regions)
    {
        if (lines.Count == 0) return [];
        if (regions.Count == 0) return GroupLines(lines);

        const double MinLineCoverage = 0.55;

        var orderedRegions = regions
            .OrderBy(r => r.BoundingRect.Y)
            .ThenBy(r => r.BoundingRect.X)
            .ToList();

        var assigned = new Dictionary<LayoutRegion, List<OcrLine>>();
        var unassigned = new List<OcrLine>();

        foreach (var line in lines)
        {
            double lineArea = line.BoundingRect.Width * line.BoundingRect.Height;
            LayoutRegion? best = null;
            double bestCoverage = 0;

            foreach (var region in orderedRegions)
            {
                var overlap = Rect.Intersect(line.BoundingRect, region.BoundingRect);
                if (overlap.IsEmpty || lineArea <= 0) continue;

                double coverage = (overlap.Width * overlap.Height) / lineArea;
                if (coverage > bestCoverage)
                {
                    bestCoverage = coverage;
                    best = region;
                }
            }

            if (best != null && bestCoverage >= MinLineCoverage)
            {
                if (!assigned.TryGetValue(best, out var list))
                    assigned[best] = list = [];
                list.Add(line);
            }
            else
            {
                unassigned.Add(line);
            }
        }

        var blocks = new List<OcrBlock>();
        foreach (var region in orderedRegions)
        {
            if (!assigned.TryGetValue(region, out var regionLines) || regionLines.Count == 0) continue;
            blocks.AddRange(GroupLinesInRegion(regionLines));
        }

        if (unassigned.Count > 0)
            blocks.AddRange(GroupLines(unassigned));

        return blocks.OrderBy(b => b.BoundingRect.Y).ThenBy(b => b.BoundingRect.X).ToList();
    }

    // Gap-based paragraph splitter for lines within a single layout region.
    // Phase 1: merge consecutive lines whose vertical gap is below mergeThreshold.
    //   - 2-line regions: mergeThreshold = avgHeight (can't infer spacing from 1 gap;
    //     use line height as reference, consistent with ShouldMerge's localH * 1.0 rule).
    //   - 3+ line regions: mergeThreshold = smallestGap * 1.5 — smallest gap is normal
    //     line spacing; paragraph breaks must be > 1.5× that to separate blocks.
    // Phase 2: within each merged group, split after any non-last line whose width is
    //          < 55% of the group's widest line (signals a forced Enter / paragraph end).
    private static List<OcrBlock> GroupLinesInRegion(List<OcrLine> lines)
    {
        if (lines.Count <= 1) return lines.Count == 0 ? [] : [new OcrBlock { Lines = lines }];

        var sorted = lines.OrderBy(l => l.BoundingRect.Y).ToList();
        int n = sorted.Count;

        double avgHeight = sorted.Average(l => l.BoundingRect.Height);

        double mergeThreshold = getGroupMergeThreshold(lines);
        //Console.WriteLine($"Grouping {n} lines in region: avg height = {avgHeight:F1}, merge threshold = {mergeThreshold:F1}");
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

        // Only consecutive pairs need checking — list is sorted by Y, so non-consecutive
        // pairs always exceed mergeThreshold and can only merge transitively anyway.
        for (int i = 0; i < n - 1; i++)
        {
            if (IsNewParagraphStart(sorted[i + 1].FullText, sorted[i].FullText)) continue;
            double gap = sorted[i + 1].BoundingRect.Y - (sorted[i].BoundingRect.Y + sorted[i].BoundingRect.Height);
            //Console.WriteLine($"Region line {i} Bottom Y = {sorted[i].BoundingRect.Y + sorted[i].BoundingRect.Height}, line {i + 1} top Y = {sorted[i + 1].BoundingRect.Y:F1}");
            //Console.WriteLine($"  → Gap between lines {i} and {i + 1}: {gap:F1}");
            if (gap <= mergeThreshold)
                Union(i, i + 1);
        }

        var groups = Enumerable.Range(0, n)
            .GroupBy(Find)
            .Select(g => g.Select(i => sorted[i]).OrderBy(l => l.BoundingRect.Y).ToList())
            .ToList();

        var result = new List<OcrBlock>();
        foreach (var group in groups)
            result.AddRange(SplitOnShortLines(group));
        return result;
    }

    private static List<OcrBlock> SplitOnShortLines(List<OcrLine> lines)
    {
        if (lines.Count <= 1) return [new OcrBlock { Lines = lines }];

        double maxWidth = lines.Max(l => l.BoundingRect.Width);
        var blocks = new List<OcrBlock>();
        int start = 0;

        for (int i = 0; i < lines.Count - 1; i++)
        {
            // A non-last line that is much narrower than the block's widest line signals
            // a paragraph end (manual Enter or ragged last line). Split after it.
            if (lines[i].BoundingRect.Width < maxWidth * 0.55)
            {
                blocks.Add(new OcrBlock { Lines = lines.GetRange(start, i - start + 1) });
                start = i + 1;
            }
        }

        blocks.Add(new OcrBlock { Lines = lines.GetRange(start, lines.Count - start) });
        return blocks;
    }

    /// Returns Y-expanded rects for each line assigned to a layout region, showing
    /// the merge-zone used by GroupLinesInRegion. Two consecutive zones that overlap
    /// in Y (and share X) correspond to a merge. Used by Ctrl+1 debug overlay.
    public static List<Rect> GetMergeZoneRects(List<OcrLine> lines, List<LayoutRegion> regions)
    {
        const double MinLineCoverage = 0.55;
        var assigned = new Dictionary<LayoutRegion, List<OcrLine>>();

        foreach (var line in lines)
        {
            double lineArea = line.BoundingRect.Width * line.BoundingRect.Height;
            LayoutRegion? best = null;
            double bestCoverage = 0;
            foreach (var region in regions)
            {
                var overlap = Rect.Intersect(line.BoundingRect, region.BoundingRect);
                if (overlap.IsEmpty || lineArea <= 0) continue;
                double coverage = overlap.Width * overlap.Height / lineArea;
                if (coverage > bestCoverage) { bestCoverage = coverage; best = region; }
            }
            if (best != null && bestCoverage >= MinLineCoverage)
            {
                if (!assigned.TryGetValue(best, out var list)) assigned[best] = list = [];
                list.Add(line);
            }
        }

        var result = new List<Rect>();
        foreach (var (_, regionLines) in assigned)
        {
            var sorted = regionLines.OrderBy(l => l.BoundingRect.Y).ToList();
            int n = sorted.Count;
            if (n < 2) continue;

            double mergeThreshold = getGroupMergeThreshold(regionLines);
            
            double expand = mergeThreshold / 2.0;
            foreach (var line in sorted)
            {
                var r = line.BoundingRect;
                result.Add(new Rect(r.X, r.Y - expand, r.Width, r.Height + expand * 2));
            }
        }
        return result;
    }

    private static double getGroupMergeThreshold(List<OcrLine> lines)
    {
        if (lines.Count <= 1) return double.MaxValue;

        var sorted = lines.OrderBy(l => l.BoundingRect.Y).ToList();
        double avgHeight = sorted.Average(l => l.BoundingRect.Height);

        var gaps = new List<double>();
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            double g = sorted[i + 1].BoundingRect.Y - (sorted[i].BoundingRect.Y + sorted[i].BoundingRect.Height);
            if (g > avgHeight * 0.1)   // lọc overlap và gap rác OCR
                gaps.Add(g);
        }

        if (gaps.Count == 0) return avgHeight;

        gaps.Sort();
        double medianGap = gaps[gaps.Count / 2];   // median, không phải min

        return medianGap * 1.5;   // 1.5× thay vì 2× vì median đã cao hơn min
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
    // True if any fence line lies strictly between upper and lower in Y and is
    // horizontally aligned with both. Used to block A–C merges when fence B is between them.
    private static bool HasAlignedFenceBetween(OcrLine upper, OcrLine lower, List<OcrLine> allLines)
    {
        double upperBottom = upper.BoundingRect.Y + upper.BoundingRect.Height;
        double lowerTop = lower.BoundingRect.Y;
        foreach (var k in allLines)
        {
            if (!IsNewParagraphStart(k.FullText)) continue;
            double kY = k.BoundingRect.Y;
            if (kY <= upperBottom || kY >= lowerTop) continue;
            double lh1 = Math.Max(upper.BoundingRect.Height, k.BoundingRect.Height);
            double hOverlap1 = Math.Min(upper.BoundingRect.Right, k.BoundingRect.Right) - Math.Max(upper.BoundingRect.Left, k.BoundingRect.Left);
            if (hOverlap1 < -lh1 * 0.5) continue;
            double lh2 = Math.Max(k.BoundingRect.Height, lower.BoundingRect.Height);
            double hOverlap2 = Math.Min(k.BoundingRect.Right, lower.BoundingRect.Right) - Math.Max(k.BoundingRect.Left, lower.BoundingRect.Left);
            if (hOverlap2 < -lh2 * 0.5) continue;
            return true;
        }
        return false;
    }

    // previousText: the line directly above in Y order.
    // Uppercase start only signals a new paragraph when the previous line ends with '.'
    // (sentence boundary). Without context (fence detection), uppercase still acts as fence.
    private static bool IsNewParagraphStart(string text, string? previousText = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        char first = text.TrimStart()[0];
        if (first is '-' or '.' or '•' or '*' or '·') return true;
        if (!char.IsUpper(first)) return false;
        if (previousText is null) return true; // no context → keep old behaviour
        return previousText.TrimEnd().EndsWith('.');
    }

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
