namespace TransIt.Services;

public static class FontSizeEstimator
{
    // OCR bounding box height in logical pixels → WPF font size (DIPs).
    // Factor ~0.72 accounts for line-height padding included in bounding box.
    public static double Estimate(double boundingHeightLogical) =>
        Math.Clamp(boundingHeightLogical * 0.72, 8.0, 96.0);
}
