using System.Drawing;
using TransIt.Models;

namespace TransIt.Core;

public static class LayoutGrouping
{
    public static async Task<List<OcrBlock>> GroupLinesAsync(
        List<OcrLine> lines, LayoutService? layout,
        Bitmap bitmap, CancellationToken ct = default)
    {
        if (layout is null)
            return OcrBlock.GroupLines(lines);

        try
        {
            var regions = await layout.DetectAsync(bitmap, ct);
            return OcrBlock.GroupLinesWithLayout(lines, regions);
        }
        catch
        {
            return OcrBlock.GroupLines(lines); // layout failure must never break translation
        }
    }
}
