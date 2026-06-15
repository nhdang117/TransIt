namespace TransIt.Services;

public static class FontSizeEstimator
{
    // OCR tight-box height (logical DIPs) → WPF FontSize (DIPs).
    //
    // The OCR engine returns a tight ink bounding box whose height relative to
    // the font em size depends on which characters appear in the line:
    //
    //   No descenders (caps, ascenders only): box ≈ 0.70 × em  → factor 1/0.70 ≈ 1.43
    //   Has descenders (g j p q y):           box ≈ 0.95 × em  → factor 1/0.95 ≈ 1.05
    //
    // Using the wrong factor causes the overlay to look 30-40 % too large or
    // too small. The text parameter lets us pick the correct factor per block.
    private static readonly char[] _descenders = ['g', 'j', 'p', 'q', 'y'];

    public static double Estimate(double lineHeightLogical, string blockText)
    {
        bool hasDescenders = blockText.IndexOfAny(_descenders) >= 0;
        double factor = hasDescenders ? 1.05 : 1.43;
        return Math.Clamp(lineHeightLogical * factor, 12.0, 96.0);
    }
}
