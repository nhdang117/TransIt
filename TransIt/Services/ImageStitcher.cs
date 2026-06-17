using OpenCvSharp;

namespace TransIt.Services;

public static class ImageStitcher
{
    /// <summary>
    /// Stitches JPEG frames into one tall image using OpenCV's Stitcher (Scans mode).
    /// Scans mode uses feature matching (ORB) with pure-translation estimation — no
    /// perspective warping — which is correct for screen scroll captures.
    /// Falls back to plain vconcat if Stitcher fails.
    /// </summary>
    public static byte[] StitchVertically(IReadOnlyList<byte[]> jpegFrames)
    {
        if (jpegFrames.Count == 0) return Array.Empty<byte>();
        if (jpegFrames.Count == 1) return jpegFrames[0];

        var mats = jpegFrames.Select(f => Cv2.ImDecode(f, ImreadModes.Color)).ToList();
        try
        {
            using var stitcher = Stitcher.Create(Stitcher.Mode.Scans);
            using var result = new Mat();
            var status = stitcher.Stitch(mats, result);

            if (status == Stitcher.Status.OK)
            {
                Cv2.ImEncode(".jpg", result, out var buf,
                    new int[] { (int)ImwriteFlags.JpegQuality, 85 });
                return buf;
            }

            // Fallback: plain vconcat (duplicate content at borders, but usable).
            return VConcat(mats);
        }
        finally
        {
            foreach (var m in mats) m.Dispose();
        }
    }

    private static byte[] VConcat(List<Mat> mats)
    {
        int width = mats[0].Cols;
        var strips = mats.Select(m =>
        {
            if (m.Cols == width) return m.Clone();
            double s = (double)width / m.Cols;
            return m.Resize(new Size(width, (int)(m.Rows * s)));
        }).ToList();
        try
        {
            using var result = new Mat();
            Cv2.VConcat(strips, result);
            Cv2.ImEncode(".jpg", result, out var buf,
                new int[] { (int)ImwriteFlags.JpegQuality, 85 });
            return buf;
        }
        finally
        {
            foreach (var s in strips) s.Dispose();
        }
    }
}
