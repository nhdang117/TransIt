using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using OpenCvSharp;
using Sdcb.PaddleDetection;
using TransIt.Models;

namespace TransIt.Core;

public class LayoutService : IDisposable
{
    private const float MinConfidence = 0.5f;

    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private PaddleDetector? _detector;
    private bool _disposed;

    public async Task<List<LayoutRegion>> DetectAsync(Bitmap bitmap, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var detector = await GetDetectorAsync(ct);
        ct.ThrowIfCancellationRequested();

        using var mat = BitmapToMat(bitmap);
        DetectionResult[] results = await Task.Run(() => detector.Run(mat), ct);

        return results
            .Where(r => r.Confidence >= MinConfidence)
            .Select(r => new LayoutRegion
            {
                Category = MapCategory(r.LabelName),
                BoundingRect = new System.Windows.Rect(r.Rect.X, r.Rect.Y, r.Rect.Width, r.Rect.Height),
                Confidence = r.Confidence,
            })
            .ToList();
    }

    private async Task<PaddleDetector> GetDetectorAsync(CancellationToken ct)
    {
        if (_detector != null) return _detector;

        await _engineLock.WaitAsync(ct);
        try
        {
            if (_detector != null) return _detector;

            string modelDir = Path.Combine(AppContext.BaseDirectory,
                "Assets", "LayoutModel", "picodet_lcnet_x1_0_fgd_layout_infer");
            string cfgPath = Path.Combine(modelDir, "infer_cfg.yml");

            _detector = new PaddleDetector(modelDir, cfgPath, cfg => cfg.MkldnnEnabled = true);
            return _detector;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    // PNG round-trip is the simplest correct Bitmap -> Mat conversion; avoids touching
    // ScreenCaptureService's Bitmap-based capture path for the sake of this one consumer.
    private static Mat BitmapToMat(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
    }

    private static LayoutCategory MapCategory(string labelName) => labelName.ToLowerInvariant() switch
    {
        "title" => LayoutCategory.Title,
        "list" => LayoutCategory.List,
        "table" => LayoutCategory.Table,
        "figure" => LayoutCategory.Figure,
        _ => LayoutCategory.Text,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detector?.Dispose();
        _engineLock.Dispose();
    }
}
