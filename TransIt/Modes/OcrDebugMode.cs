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

/// Ctrl+1 test mode: captures full monitor, runs layout detection in background,
/// shows picker overlay where layout regions (green) or window panels (blue) can be selected.
/// After selection runs OCR+layout debug on the crop.
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
        // 1. Capture full monitor at cursor (fast BitBlt)
        var (monBitmap, captureDpi, captureMonRect) = ScreenCaptureService.CaptureMonitorAtCursor();

        try
        {
            // 2. Show picker immediately — panel-hover mode active right away
            var tcs = new TaskCompletionSource<bool>();
            WindowPickerOverlay picker = null!;
            Application.Current.Dispatcher.Invoke(() =>
            {
                _overlay.Hide();
                if (_activePane != null) { _activePane.Close(); _activePane = null; }
                picker = new WindowPickerOverlay();
                picker.Closed += (_, _) => tcs.TrySetResult(true);
                picker.Show();
            });

            // 3. Enumerate all visible windows on this monitor, crop each one, run
            //    PaddleStructure layout detection, and feed text/table rects into the
            //    picker progressively so hover targets appear as each window is processed.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_layout == null) return;

                    var windows = EnumerateWindowsOnMonitor(captureMonRect);
                    double monLogX = captureMonRect.Left / captureDpi;
                    double monLogY = captureMonRect.Top  / captureDpi;
                    var fineLogical = new List<System.Windows.Rect>();
                    int sentCount = 0;

                    foreach (var winPhysRect in windows)
                    {
                        if (tcs.Task.IsCompleted) return;

                        var clipped = System.Drawing.Rectangle.Intersect(winPhysRect, captureMonRect);
                        int bx = clipped.X - captureMonRect.Left;
                        int by = clipped.Y - captureMonRect.Top;
                        int bw = Math.Min(clipped.Width,  monBitmap.Width  - bx);
                        int bh = Math.Min(clipped.Height, monBitmap.Height - by);
                        if (bw <= 0 || bh <= 0) continue;

                        using var crop = monBitmap.Clone(
                            new System.Drawing.Rectangle(bx, by, bw, bh), monBitmap.PixelFormat);

                        List<LayoutRegion> regions;
                        try { regions = await _layout.DetectAsync(crop, ct); }
                        catch { continue; }

                        foreach (var sub in regions)
                        {
                            if (sub.Category == LayoutCategory.Figure) continue;
                            if (sub.Confidence < 0.7f) continue;
                            fineLogical.Add(new System.Windows.Rect(
                                (sub.BoundingRect.X + bx) / captureDpi + monLogX,
                                (sub.BoundingRect.Y + by) / captureDpi + monLogY,
                                sub.BoundingRect.Width  / captureDpi,
                                sub.BoundingRect.Height / captureDpi));
                        }

                        if (fineLogical.Count > sentCount)
                        {
                            var newRects = fineLogical.Skip(sentCount).ToList();
                            sentCount = fineLogical.Count;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                if (!tcs.Task.IsCompleted)
                                    picker.AddLayoutRects(newRects, captureDpi);
                            });
                        }
                    }
                }
                catch { /* layout failed — picker still usable in panel mode */ }
            }, ct);

            await tcs.Task;
            if (picker.Cancelled || picker.SelectedPhysRect is null) return;
            ct.ThrowIfCancellationRequested();

            var physRect = picker.SelectedPhysRect.Value;

            if (picker.IsLayoutRegion)
            {
                // Layout region selected — reuse pre-captured monitor bitmap
                var background = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(monBitmap));
                Application.Current.Dispatcher.Invoke(() => _overlay.ShowLoadingOverlay(background));

                int bx = Math.Max(0, physRect.X - captureMonRect.Left);
                int by = Math.Max(0, physRect.Y - captureMonRect.Top);
                int bw = Math.Max(1, Math.Min(physRect.Width,  monBitmap.Width  - bx));
                int bh = Math.Max(1, Math.Min(physRect.Height, monBitmap.Height - by));
                using var regionBitmap = monBitmap.Clone(
                    new Rectangle(bx, by, bw, bh), monBitmap.PixelFormat);

                await RunDebugAsync(regionBitmap, bx, by, captureMonRect, captureDpi, background, ct);
            }
            else
            {
                // Window panel selected — capture the relevant monitor fresh
                var (monRect, dpiScale) = DpiHelper.GetMonitorAtPoint(
                    physRect.X + physRect.Width / 2, physRect.Y + physRect.Height / 2);
                using var fullBitmap = ScreenCaptureService.CaptureRegion(monRect);
                var background = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(fullBitmap));
                Application.Current.Dispatcher.Invoke(() => _overlay.ShowLoadingOverlay(background));

                int bx = Math.Max(0, physRect.X - monRect.Left);
                int by = Math.Max(0, physRect.Y - monRect.Top);
                int bw = Math.Max(1, Math.Min(physRect.Width,  fullBitmap.Width  - bx));
                int bh = Math.Max(1, Math.Min(physRect.Height, fullBitmap.Height - by));
                using var regionBitmap = fullBitmap.Clone(
                    new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);

                await RunDebugAsync(regionBitmap, bx, by, monRect, dpiScale, background, ct);
            }
        }
        catch
        {
            Application.Current.Dispatcher.Invoke(() => _overlay.HideOverlay());
            throw;
        }
        finally
        {
            monBitmap.Dispose();
        }
    }

    private async Task RunDebugAsync(
        Bitmap regionBitmap,
        int bx, int by,
        System.Drawing.Rectangle monRect, double dpiScale,
        BitmapSource background,
        CancellationToken ct)
    {
        try
        {
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

            List<LayoutRegion> regions = [];
            string? layoutError = null;
            if (_layout != null)
            {
                try { regions = await _layout.DetectAsync(regionBitmap, ct); }
                catch (Exception ex) { layoutError = ex.Message; }
            }

            foreach (var region in regions)
                region.BoundingRect = Offset(region.BoundingRect, bx, by);

            var blocks = regions.Count > 0
                ? OcrBlock.GroupLinesWithLayout(lines, regions)
                : OcrBlock.GroupLines(lines);

            double monLogX = monRect.Left / dpiScale;
            double monLogY = monRect.Top  / dpiScale;

            System.Windows.Rect ToLogical(System.Windows.Rect physical) => new(
                physical.X / dpiScale + monLogX, physical.Y / dpiScale + monLogY,
                physical.Width / dpiScale, physical.Height / dpiScale);

            var lineRects      = lines.Select(l => ToLogical(l.BoundingRect)).ToList();
            var blockRects     = blocks.Select(b => ToLogical(b.BoundingRect)).ToList();
            var regionRects    = regions.Select(r => ToLogical(r.BoundingRect)).ToList();
            var mergeZoneRects = regions.Count > 0
                ? OcrBlock.GetMergeZoneRects(lines, regions).Select(ToLogical).ToList()
                : [];

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
                _overlay.ShowDebugOverlay(lineRects, blockRects, background, regionRects, mergeZoneRects);

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

    private static List<System.Drawing.Rectangle> EnumerateWindowsOnMonitor(System.Drawing.Rectangle monRect)
    {
        var results = new List<System.Drawing.Rectangle>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.IsIconic(hwnd)) return true;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            NativeMethods.GetWindowRect(hwnd, out var r);
            var intersection = System.Drawing.Rectangle.Intersect(r.ToRectangle(), monRect);
            if (intersection.Width < 50 || intersection.Height < 50) return true;
            results.Add(r.ToRectangle());
            return true;
        }, IntPtr.Zero);
        return results;
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
