using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TransIt.Models;

namespace TransIt.Core;

// Table structure recognition via Microsoft Table Transformer (TATR).
// Expected model: table-structure-recognition ONNX with:
//   Input  "pixel_values"  [1, 3, H, W]  (ImageNet-normalised, variable size)
//   Output "logits"        [1, N, nc]    (raw class logits; last class = no-object)
//          "pred_boxes"    [1, N, 4]     (cx,cy,w,h normalised [0,1])
//
// Expected class order (no-object is last):
//   0=table-column  1=table-row  2=col-header  3=proj-row-header  4=spanning-cell  5=no-object
// Adjust ColClass / RowClass constants if your model differs.
public class TableTransformerService : IDisposable
{
    private const int ColClass  = 0;
    private const int RowClass  = 1;
    private const float StructureThreshold = 0.5f;
    private const int MaxSide = 1000;

    private static readonly float[] ImgNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImgNetStd  = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;
    private bool _disposed;

    public TableTransformerService(string modelPath)
    {
        var opts = new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
        _session = new InferenceSession(modelPath, opts);
    }

    public void Warmup()
    {
        using var dummy = new Bitmap(64, 64, PixelFormat.Format24bppRgb);
        ExtractCells(dummy);
    }

    public Task<List<TableCell>> ExtractCellsAsync(Bitmap bitmap, CancellationToken ct = default)
        => Task.Run(() => ExtractCells(bitmap), ct);

    private List<TableCell> ExtractCells(Bitmap bitmap)
    {
        int origW = bitmap.Width;
        int origH = bitmap.Height;

        // Resize keeping aspect ratio so longest side ≤ MaxSide.
        float scale  = Math.Min((float)MaxSide / origW, (float)MaxSide / origH);
        int   netW   = (int)(origW * scale);
        int   netH   = (int)(origH * scale);

        var tensor = new DenseTensor<float>([1, 3, netH, netW]);

        using var resized = new Bitmap(netW, netH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(bitmap, 0, 0, netW, netH);
        }

        var bd = resized.LockBits(new Rectangle(0, 0, netW, netH),
                                   ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* src    = (byte*)bd.Scan0;
                int   stride = bd.Stride;
                for (int y = 0; y < netH; y++)
                {
                    byte* row = src + y * stride;
                    for (int x = 0; x < netW; x++)
                    {
                        int i = x * 3;
                        tensor[0, 0, y, x] = (row[i + 2] / 255f - ImgNetMean[0]) / ImgNetStd[0]; // R
                        tensor[0, 1, y, x] = (row[i + 1] / 255f - ImgNetMean[1]) / ImgNetStd[1]; // G
                        tensor[0, 2, y, x] = (row[i + 0] / 255f - ImgNetMean[2]) / ImgNetStd[2]; // B
                    }
                }
            }
        }
        finally { resized.UnlockBits(bd); }

        using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", tensor)]);

        var logitsTensor = outputs.First(o => o.Name == "logits").AsTensor<float>();
        var boxesTensor  = outputs.First(o => o.Name == "pred_boxes").AsTensor<float>();

        int numQueries = logitsTensor.Dimensions[1];
        int numClasses = logitsTensor.Dimensions[2];
        int noObjClass = numClasses - 1; // last class = no-object

        var rows = new List<System.Windows.Rect>();
        var cols = new List<System.Windows.Rect>();

        for (int q = 0; q < numQueries; q++)
        {
            // Softmax over logits.
            var logits = new float[numClasses];
            float maxL = float.MinValue;
            for (int c = 0; c < numClasses; c++) { logits[c] = logitsTensor[0, q, c]; if (logits[c] > maxL) maxL = logits[c]; }
            float sumE = 0;
            for (int c = 0; c < numClasses; c++) { logits[c] = MathF.Exp(logits[c] - maxL); sumE += logits[c]; }
            for (int c = 0; c < numClasses; c++) logits[c] /= sumE;

            // Best class excluding no-object.
            float bestConf = 0;
            int   bestCls  = -1;
            for (int c = 0; c < noObjClass; c++)
                if (logits[c] > bestConf) { bestConf = logits[c]; bestCls = c; }

            if (bestConf < StructureThreshold || bestCls < 0) continue;

            // Decode box: normalised (cx,cy,w,h) → original image pixel coords.
            float cx = boxesTensor[0, q, 0] * origW;
            float cy = boxesTensor[0, q, 1] * origH;
            float bw = boxesTensor[0, q, 2] * origW;
            float bh = boxesTensor[0, q, 3] * origH;
            var   box = new System.Windows.Rect(
                Math.Max(0, cx - bw / 2), Math.Max(0, cy - bh / 2),
                Math.Min(bw, origW), Math.Min(bh, origH));

            if (box.Width < 1 || box.Height < 1) continue;

            if (bestCls == ColClass) cols.Add(box);
            else if (bestCls == RowClass) rows.Add(box);
        }

        // Sort rows top-to-bottom, cols left-to-right.
        rows = [.. rows.OrderBy(r => r.Y + r.Height / 2)];
        cols = [.. cols.OrderBy(c => c.X + c.Width / 2)];

        // Create cell grid from row × column intersections.
        var cells = new List<TableCell>();
        for (int ri = 0; ri < rows.Count; ri++)
        {
            for (int ci = 0; ci < cols.Count; ci++)
            {
                var cell = System.Windows.Rect.Intersect(rows[ri], cols[ci]);
                if (cell.IsEmpty || cell.Width < 1 || cell.Height < 1) continue;
                cells.Add(new TableCell { Row = ri, Col = ci, BoundingRect = cell });
            }
        }

        return cells;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
