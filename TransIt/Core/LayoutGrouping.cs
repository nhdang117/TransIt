using System.Drawing;
using TransIt.Models;

namespace TransIt.Core;

public static class LayoutGrouping
{
    // dx/dy: bitmap may be a crop of the surface lines' rects are expressed in (e.g.
    // RegionMode offsets lines to full-monitor space before calling this with a cropped
    // regionBitmap) - DetectAsync returns rects relative to bitmap's own origin, so they need
    // the same offset or every line/region overlap check in GroupLinesWithLayout compares
    // mismatched origins. Callers operating on a full, uncropped capture pass the default 0,0.
    public static async Task<List<OcrBlock>> GroupLinesAsync(
        List<OcrLine> lines, LayoutService? layout,
        Bitmap bitmap, CancellationToken ct = default, double dx = 0, double dy = 0)
    {
        if (layout is null)
            return OcrBlock.GroupLines(lines);

        try
        {
            var regions = await layout.DetectAsync(bitmap, ct);
            foreach (var region in regions)
                region.BoundingRect = new System.Windows.Rect(
                    region.BoundingRect.X + dx, region.BoundingRect.Y + dy,
                    region.BoundingRect.Width, region.BoundingRect.Height);
            return OcrBlock.GroupLinesWithLayout(lines, regions);
        }
        catch
        {
            return OcrBlock.GroupLines(lines); // layout failure must never break translation
        }
    }
}
