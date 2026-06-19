using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TransIt.Models;

namespace TransIt.Core;

// DocLayout-YOLO document layout detection (DocStructBench model, 1024×1024 input).
// Model: wybxc/DocLayout-YOLO-DocStructBench-onnx on Hugging Face
// Output shape: [1, nc+4, 21504]  (cx,cy,w,h + 10 class scores per anchor)
//
// Class → LayoutCategory:
//   0=title  1=text  2=abandon(null,skip)  3=figure  4=figure_caption→Text
//   5=table  6=table_caption→Text  7=table_footnote→Text  8=isolate_formula→Figure  9=formula_caption→Text
public class YoloLayoutService : IDisposable
{
    private const int InputSize = 1024;
    private const float ConfThreshold = 0.20f;
    private const float NmsThreshold  = 0.45f;

    // null = skip (abandon class — irrelevant content, never shown to user).
    private static readonly LayoutCategory?[] ClassMap =
    [
        LayoutCategory.Title,   // 0 title
        LayoutCategory.Text,    // 1 plain text
        null,                   // 2 abandon
        LayoutCategory.Figure,  // 3 figure
        LayoutCategory.Text,    // 4 figure_caption
        LayoutCategory.Table,   // 5 table
        LayoutCategory.Text,    // 6 table_caption
        LayoutCategory.Text,    // 7 table_footnote
        LayoutCategory.Figure,  // 8 isolate_formula
        LayoutCategory.Text,    // 9 formula_caption
    ];

    private readonly InferenceSession _session;
    private bool _disposed;

    public YoloLayoutService(string modelPath)
    {
        var opts = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        _session = new InferenceSession(modelPath, opts);
    }

    // Populated after each Detect call — useful for Ctrl+1 debug output.
    public string LastDebugInfo { get; private set; } = "";

    // Run a dummy 1×1 inference so ONNX JIT-compiles the graph before first real use.
    public void Warmup()
    {
        using var dummy = new System.Drawing.Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        Detect(dummy);
    }

    public Task<List<LayoutRegion>> DetectAsync(Bitmap bitmap, CancellationToken ct = default)
        => Task.Run(() => Detect(bitmap), ct);

    // Takes ownership of bitmap — disposes it after detection completes.
    // Use when starting in parallel with other code that still holds the original bitmap.
    public Task<List<LayoutRegion>> DetectOwnedAsync(Bitmap bitmap, CancellationToken ct = default)
        => Task.Run(() => { using (bitmap) return Detect(bitmap); }, ct);

    private List<LayoutRegion> Detect(Bitmap bitmap)
    {
        int origW = bitmap.Width;
        int origH = bitmap.Height;

        // Letterbox-resize to InputSize×InputSize with grey padding.
        float scale   = Math.Min((float)InputSize / origW, (float)InputSize / origH);
        int   scaledW = (int)(origW * scale);
        int   scaledH = (int)(origH * scale);
        int   padX    = (InputSize - scaledW) / 2;
        int   padY    = (InputSize - scaledH) / 2;

        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);

        using var resized = new Bitmap(InputSize, InputSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.Clear(Color.FromArgb(114, 114, 114));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(bitmap, padX, padY, scaledW, scaledH);
        }

        var bd = resized.LockBits(new Rectangle(0, 0, InputSize, InputSize),
                                   ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* src    = (byte*)bd.Scan0;
                int   stride = bd.Stride;
                for (int y = 0; y < InputSize; y++)
                {
                    byte* row = src + y * stride;
                    for (int x = 0; x < InputSize; x++)
                    {
                        int i = x * 3;
                        tensor[0, 0, y, x] = row[i + 2] / 255f; // R
                        tensor[0, 1, y, x] = row[i + 1] / 255f; // G
                        tensor[0, 2, y, x] = row[i + 0] / 255f; // B
                    }
                }
            }
        }
        finally { resized.UnlockBits(bd); }

        string inputName = _session.InputNames[0];
        using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);

        var raw    = outputs[0].AsTensor<float>();
        int nc     = ClassMap.Length;
        string shapeStr = $"[{string.Join(",", Enumerable.Range(0, raw.Dimensions.Length).Select(i => raw.Dimensions[i]))}]";

        // ── Format A: YOLOv10 end2end NMS ──────────────────────────────────
        // Shape [1, max_det, 6]: each row = x1,y1,x2,y2,conf,cls_id (xyxy pixel space).
        // NMS already applied by model — return directly, no manual NMS needed.
        if (raw.Dimensions.Length == 3 && raw.Dimensions[2] == 6)
        {
            int maxDet = raw.Dimensions[1];
            LastDebugInfo = $"YOLO fmt=end2end shape={shapeStr} bitmap={origW}×{origH} scale={scale:F3} pad=({padX},{padY})";

            var result = new List<LayoutRegion>();
            for (int b = 0; b < maxDet; b++)
            {
                float conf = raw[0, b, 4];
                if (conf < ConfThreshold) continue;

                int cls = (int)raw[0, b, 5];
                if (cls < 0 || cls >= ClassMap.Length || ClassMap[cls] is null) continue;

                float rx1 = raw[0, b, 0];
                float ry1 = raw[0, b, 1];
                float rx2 = raw[0, b, 2];
                float ry2 = raw[0, b, 3];

                // Detect normalized [0,1] vs pixel [0,InputSize].
                if (rx2 <= 1.5f && ry2 <= 1.5f)
                { rx1 *= InputSize; ry1 *= InputSize; rx2 *= InputSize; ry2 *= InputSize; }

                // Unletterbox xyxy from padded 1024×1024 back to original bitmap pixels.
                float x1o = Math.Clamp((rx1 - padX) / scale, 0, origW);
                float y1o = Math.Clamp((ry1 - padY) / scale, 0, origH);
                float x2o = Math.Clamp((rx2 - padX) / scale, 0, origW);
                float y2o = Math.Clamp((ry2 - padY) / scale, 0, origH);

                if (x2o - x1o < 2 || y2o - y1o < 2) continue;

                result.Add(new LayoutRegion
                {
                    Category     = ClassMap[cls]!.Value,
                    BoundingRect = new System.Windows.Rect(x1o, y1o, x2o - x1o, y2o - y1o),
                    Confidence   = conf,
                });
            }
            return result;
        }

        // ── Format B: YOLOv8 raw anchors ───────────────────────────────────
        // [1, nc+4, N] channels-first  OR  [1, N, nc+4] channels-last.
        // cxcywh pixel space; manual NMS required.
        bool transposed;
        int  numBoxes;

        if (raw.Dimensions.Length == 3 && raw.Dimensions[1] == nc + 4)
        { transposed = false; numBoxes = raw.Dimensions[2]; }
        else if (raw.Dimensions.Length == 3 && raw.Dimensions[2] == nc + 4)
        { transposed = true;  numBoxes = raw.Dimensions[1]; }
        else
        {
            LastDebugInfo = $"YOLO fmt=unknown shape={shapeStr} nc+4={nc + 4}";
            return [];
        }

        LastDebugInfo = $"YOLO fmt={(transposed?"v8-T":"v8")} shape={shapeStr} bitmap={origW}×{origH} scale={scale:F3} pad=({padX},{padY})";

        float V(int ch, int b) => transposed ? raw[0, b, ch] : raw[0, ch, b];

        var candidates = new List<(System.Windows.Rect box, int cls, float conf)>(256);
        for (int b = 0; b < numBoxes; b++)
        {
            float bestConf = 0;
            int   bestCls  = -1;
            for (int c = 0; c < nc; c++)
            {
                float s = V(4 + c, b);
                if (s > bestConf) { bestConf = s; bestCls = c; }
            }
            if (bestConf < ConfThreshold || bestCls < 0) continue;
            if (ClassMap[bestCls] is null) continue;

            float cx = V(0, b);
            float cy = V(1, b);
            float bw = V(2, b);
            float bh = V(3, b);

            // Detect normalized coords.
            if (cx <= 1.5f && cy <= 1.5f)
            { cx *= InputSize; cy *= InputSize; bw *= InputSize; bh *= InputSize; }

            // Unletterbox cxcywh from padded 1024×1024 back to original bitmap pixels.
            float x1 = Math.Clamp((cx - bw / 2 - padX) / scale, 0, origW);
            float y1 = Math.Clamp((cy - bh / 2 - padY) / scale, 0, origH);
            float x2 = Math.Clamp((cx + bw / 2 - padX) / scale, 0, origW);
            float y2 = Math.Clamp((cy + bh / 2 - padY) / scale, 0, origH);

            if (x2 - x1 < 2 || y2 - y1 < 2) continue;

            candidates.Add((new System.Windows.Rect(x1, y1, x2 - x1, y2 - y1), bestCls, bestConf));
        }

        return ApplyNms(candidates);
    }

    private static List<LayoutRegion> ApplyNms(
        List<(System.Windows.Rect box, int cls, float conf)> detections)
    {
        var result = new List<LayoutRegion>();
        foreach (var group in detections.GroupBy(d => d.cls))
        {
            LayoutCategory? category = ClassMap[group.Key];
            if (category is null) continue;

            var sorted     = group.OrderByDescending(d => d.conf).ToList();
            var suppressed = new bool[sorted.Count];

            for (int i = 0; i < sorted.Count; i++)
            {
                if (suppressed[i]) continue;
                result.Add(new LayoutRegion
                {
                    Category     = category.Value,
                    BoundingRect = sorted[i].box,
                    Confidence   = sorted[i].conf,
                });
                for (int j = i + 1; j < sorted.Count; j++)
                    if (!suppressed[j] && IoU(sorted[i].box, sorted[j].box) > NmsThreshold)
                        suppressed[j] = true;
            }
        }
        return result;
    }

    private static float IoU(System.Windows.Rect a, System.Windows.Rect b)
    {
        double ix    = Math.Max(0, Math.Min(a.Right, b.Right)   - Math.Max(a.Left, b.Left));
        double iy    = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double inter = ix * iy;
        double union = a.Width * a.Height + b.Width * b.Height - inter;
        return union < 1e-6 ? 0f : (float)(inter / union);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
