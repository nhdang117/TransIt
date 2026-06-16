using System.Drawing;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TransIt.Core;
using TransIt.Infrastructure;
using TransIt.Models;
using TransIt.Windows.Overlay;
using TransIt.Windows.Selection;
using TransIt.Windows.TextPane;

namespace TransIt.Modes;

/// Ctrl+1 test mode: same region-select flow as RegionMode, but draws raw PaddleOCR
/// line boxes and OcrBlock.GroupLines paragraph boxes instead of translating, then shows
/// the exact payload (TranslatableBlock list) that would be sent to the OpenAI API.
public class OcrDebugMode : ITranslationMode
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    private readonly OcrService _ocr;
    private readonly LayoutService _layout;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private TextPaneWindow? _activePane;

    public OcrDebugMode(OcrService ocr, LayoutService layout, AppSettings settings, OverlayWindow overlay)
    {
        _ocr = ocr;
        _layout = layout;
        _settings = settings;
        _overlay = overlay;
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _overlay.Hide();
            if (_activePane != null) { _activePane.Close(); _activePane = null; }
        });

        var tcs = new TaskCompletionSource<bool>();
        RegionSelectWindow selector = null!;
        Application.Current.Dispatcher.Invoke(() =>
        {
            selector = new RegionSelectWindow();
            selector.Closed += (_, _) => tcs.TrySetResult(true);
            selector.Show();
        });
        await tcs.Task;

        if (selector.Cancelled || selector.SelectedRect is null) return;
        ct.ThrowIfCancellationRequested();

        var physRect = selector.SelectedRect.Value;
        var (monRect, dpiScale) = DpiHelper.GetMonitorAtPoint(
            physRect.X + physRect.Width / 2, physRect.Y + physRect.Height / 2);
        using var fullBitmap = ScreenCaptureService.CaptureRegion(monRect);
        var background = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(fullBitmap));

        Application.Current.Dispatcher.Invoke(() => _overlay.ShowLoadingOverlay(background));

        try
        {
            int bx = Math.Max(0, physRect.X - monRect.Left);
            int by = Math.Max(0, physRect.Y - monRect.Top);
            int bw = Math.Max(1, Math.Min(physRect.Width,  fullBitmap.Width  - bx));
            int bh = Math.Max(1, Math.Min(physRect.Height, fullBitmap.Height - by));

            using var regionBitmap = fullBitmap.Clone(new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);

            var lines = await _ocr.RecognizeAsync(regionBitmap, _settings.SourceLanguage, ct);
            if (lines.Count == 0)
            {
                Application.Current.Dispatcher.Invoke(() => _overlay.ShowDebugOverlay([], [], background));
                return;
            }

            foreach (var line in lines)
            {
                line.BoundingRect = Offset(line.BoundingRect, bx, by);
                foreach (var word in line.Words)
                    word.BoundingRect = Offset(word.BoundingRect, bx, by);
            }

            // Run layout detection directly (not via LayoutGrouping's silent-fallback
            // wrapper) so we can see exactly what the detector returned/threw.
            List<LayoutRegion> regions = [];
            string? layoutError = null;
            if (_layout != null)
            {
                try { regions = await _layout.DetectAsync(regionBitmap, ct); }
                catch (Exception ex) { layoutError = ex.Message; }
            }

            var blocks = regions.Count > 0
                ? OcrBlock.GroupLinesWithLayout(lines, regions)
                : OcrBlock.GroupLines(lines);

            double monLogX = monRect.Left / dpiScale;
            double monLogY = monRect.Top  / dpiScale;

            Rect ToLogical(Rect physical) => new(
                physical.X / dpiScale + monLogX, physical.Y / dpiScale + monLogY,
                physical.Width / dpiScale, physical.Height / dpiScale);

            var lineRects   = lines.Select(l => ToLogical(l.BoundingRect)).ToList();
            var blockRects  = blocks.Select(b => ToLogical(b.BoundingRect)).ToList();
            var regionRects = regions.Select(r => ToLogical(r.BoundingRect)).ToList();

            // Same shape RegionMode/SnapshotMode/RealtimeMode build right before calling
            // TranslationService.TranslateBlocksAsync — physical-pixel rect, not logical DIPs.
            var translatable = blocks.Select((b, i) => new TranslationService.TranslatableBlock(
                i, b.FullText, b.BoundingRect.X, b.BoundingRect.Y, b.BoundingRect.Width, b.BoundingRect.Height)).ToList();
            var payloadJson = JsonSerializer.Serialize(translatable, _jsonOpts);

            var diagLines = new List<string>
            {
                $"Layout regions detected: {regions.Count}" + (layoutError != null ? $" (ERROR: {layoutError})" : ""),
            };
            diagLines.AddRange(regions.Select(r =>
                $"  [{r.Category}] conf={r.Confidence:F2} rect={r.BoundingRect}"));
            diagLines.Add(payloadJson);

            Application.Current.Dispatcher.Invoke(() =>
            {
                _overlay.ShowDebugOverlay(lineRects, blockRects, background, regionRects);

                var pane = new TextPaneWindow();
                _activePane = pane;
                pane.Closed += (_, _) => { if (_activePane == pane) _activePane = null; };
                pane.ShowLoading();
                pane.ShowTranslation([string.Join("\n", diagLines)]);
            });
        }
        catch
        {
            Application.Current.Dispatcher.Invoke(() => _overlay.HideOverlay());
            throw;
        }
    }

    public void Deactivate() =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            _overlay.HideOverlay();
            _activePane?.Close();
            _activePane = null;
        });

    private static System.Windows.Rect Offset(System.Windows.Rect r, double dx, double dy) =>
        new(r.X + dx, r.Y + dy, r.Width, r.Height);

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        IntPtr hbmp = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hbmp, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            if (Math.Abs(src.DpiX - 96.0) < 0.5) return src;
            int stride = (src.PixelWidth * src.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[src.PixelHeight * stride];
            src.CopyPixels(pixels, stride, 0);
            return BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96,
                src.Format, src.Palette, pixels, stride);
        }
        finally
        {
            NativeMethods.DeleteObject(hbmp);
        }
    }
}
