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

    /// Group OCR lines into paragraph blocks using a vertical gap heuristic.
    public static List<OcrBlock> GroupLines(List<OcrLine> lines)
    {
        if (lines.Count == 0) return [];

        var sorted = lines.OrderBy(l => l.BoundingRect.Y).ToList();
        var blocks = new List<OcrBlock>();
        var current = new OcrBlock();
        current.Lines.Add(sorted[0]);

        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = current.Lines[^1];
            double gap = sorted[i].BoundingRect.Y - (prev.BoundingRect.Y + prev.BoundingRect.Height);
            double localH = Math.Max(prev.BoundingRect.Height, sorted[i].BoundingRect.Height);
            if (gap <= localH * 1.5)
                current.Lines.Add(sorted[i]);
            else
            {
                blocks.Add(current);
                current = new OcrBlock();
                current.Lines.Add(sorted[i]);
            }
        }
        blocks.Add(current);
        return blocks;
    }
}
