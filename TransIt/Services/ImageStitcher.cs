using OpenCvSharp;

namespace TransIt.Services;

/// <summary>
/// Incremental stitcher — call Append() for each new frame during scroll capture.
/// Maintains the growing stitched Mat and the last raw frame for overlap detection.
/// </summary>
public sealed class IncrementalStitcher : IDisposable
{
    private Mat? _stitched;
    private Mat? _lastRaw;
    private bool _disposed;

    public int FrameCount { get; private set; }

    // Appends a new JPEG frame and returns the updated stitched JPEG.
    public byte[] Append(byte[] jpegFrame)
    {
        using var curr = Cv2.ImDecode(jpegFrame, ImreadModes.Color);

        if (_stitched == null)
        {
            _stitched = curr.Clone();
        }
        else
        {
            int newStart = ImageStitcher.FindNewContentStart(_lastRaw!, curr);
            if (newStart < curr.Rows)
            {
                var strip = ImageStitcher.ResizeToWidth(curr.RowRange(newStart, curr.Rows), _stitched.Cols);
                try
                {
                    var combined = new Mat();
                    Cv2.VConcat([_stitched, strip], combined);
                    _stitched.Dispose();
                    _stitched = combined;
                }
                finally { strip.Dispose(); }
            }
        }

        _lastRaw?.Dispose();
        _lastRaw = curr.Clone();
        FrameCount++;

        Cv2.ImEncode(".jpg", _stitched, out var buf, [(int)ImwriteFlags.JpegQuality, 85]);
        return buf;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stitched?.Dispose();
        _lastRaw?.Dispose();
    }
}

public static class ImageStitcher
{
    /// <summary>
    /// Stitches JPEG frames into one tall image by detecting the overlap between consecutive
    /// frames via template matching and stacking only the new (non-overlapping) content.
    /// More reliable than OpenCV Stitcher for scroll captures: never drops frames, works
    /// even when individual frames have few features (logos, white space, etc.).
    /// </summary>
    public static byte[] StitchVertically(IReadOnlyList<byte[]> jpegFrames)
    {
        if (jpegFrames.Count == 0) return Array.Empty<byte>();
        if (jpegFrames.Count == 1) return jpegFrames[0];

        var mats = jpegFrames.Select(f => Cv2.ImDecode(f, ImreadModes.Color)).ToList();
        try
        {
            return OverlapStitch(mats);
        }
        finally
        {
            foreach (var m in mats) m.Dispose();
        }
    }

    private static byte[] OverlapStitch(List<Mat> mats)
    {
        int width = mats[0].Cols;
        var strips = new List<Mat> { ResizeToWidth(mats[0], width) };

        for (int i = 1; i < mats.Count; i++)
        {
            int newStart = FindNewContentStart(mats[i - 1], mats[i]);
            if (newStart < mats[i].Rows)
                strips.Add(ResizeToWidth(mats[i].RowRange(newStart, mats[i].Rows), width));
        }

        try
        {
            using var result = new Mat();
            Cv2.VConcat(strips, result);
            Cv2.ImEncode(".jpg", result, out var buf,
                [(int)ImwriteFlags.JpegQuality, 85]);
            return buf;
        }
        finally
        {
            foreach (var s in strips) s.Dispose();
        }
    }

    // Returns the row index in curr where content not present in prev begins.
    // Uses template matching on a horizontal strip from the lower-middle of prev.
    internal static int FindNewContentStart(Mat prev, Mat curr)
    {
        int w = Math.Min(prev.Cols, curr.Cols);

        using var prevGray = prev.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var currGray = curr.CvtColor(ColorConversionCodes.BGR2GRAY);

        // Template: rows 60%–85% of prev (avoids header/footer edge artifacts).
        int tTop = (int)(prev.Rows * 0.60);
        int tBot = (int)(prev.Rows * 0.85);
        int tH   = tBot - tTop;

        using var template = new Mat(prevGray, new Rect(0, tTop, w, tH));

        // Search only in the top 70% of curr — assumes overlap is at least 30%.
        int searchH = Math.Min(curr.Rows - tH, (int)(curr.Rows * 0.70));
        if (searchH <= 0) return 0;

        using var searchArea  = new Mat(currGray, new Rect(0, 0, w, searchH));
        using var matchResult = new Mat();
        Cv2.MatchTemplate(searchArea, template, matchResult, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(matchResult, out _, out double maxVal, out _, out Point maxLoc);

        // Low confidence → no reliable overlap found; treat entire curr as new content.
        if (maxVal < 0.6) return 0;

        // Template (prev row tTop) matched at curr row maxLoc.Y.
        // New content (below prev's last row) starts at curr row: maxLoc.Y + (prev.Rows - tTop).
        int newStart = maxLoc.Y + (prev.Rows - tTop);
        return Math.Clamp(newStart, 0, curr.Rows);
    }

    internal static Mat ResizeToWidth(Mat m, int w)
    {
        if (m.Cols == w) return m.Clone();
        double s = (double)w / m.Cols;
        return m.Resize(new Size(w, (int)(m.Rows * s)));
    }
}
