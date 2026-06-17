using System.Drawing;
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

public class RegionMode : ITranslationMode
{
    private readonly OcrService _ocr;
    private readonly LayoutService _layout;
    private readonly TranslationService _translator;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private TextPaneWindow? _activePane;

    private System.Drawing.Bitmap? _storedBitmap;
    private System.Drawing.Rectangle _storedMonRect;
    private double _storedDpiScale;

    public RegionMode(OcrService ocr, LayoutService layout, TranslationService translator,
                      AppSettings settings, OverlayWindow overlay)
    {
        _ocr = ocr;
        _layout = layout;
        _translator = translator;
        _settings = settings;
        _overlay = overlay;
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        Application.Current.Dispatcher.Invoke(() => _overlay.Hide());

        var tcs = new TaskCompletionSource<bool>();
        WindowPickerOverlay picker = null!;
        Application.Current.Dispatcher.Invoke(() =>
        {
            picker = new WindowPickerOverlay();
            picker.Closed += (_, _) => tcs.TrySetResult(true);
            picker.Show();
        });
        await tcs.Task;

        if (picker.Cancelled || picker.SelectedPhysRect is null) return;
        ct.ThrowIfCancellationRequested();

        var physRect = picker.SelectedPhysRect.Value;
        if (_settings.RegionOverlayMode)
            await RunOverlayMode(physRect, ct);
        else
            await RunTextPaneMode(physRect, ct);
    }

    private async Task RunOverlayMode(System.Drawing.Rectangle physRect, CancellationToken ct)
    {
        // Determine monitor from selection center — correct DPI even if cursor moved after hotkey.
        var (monRect, dpiScale) = DpiHelper.GetMonitorAtPoint(
            physRect.X + physRect.Width / 2, physRect.Y + physRect.Height / 2);
        using var fullBitmap = ScreenCaptureService.CaptureRegion(monRect);
        var background = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(fullBitmap));

        Application.Current.Dispatcher.Invoke(() => _overlay.ShowLoadingOverlay(background));

        try
        {
            // Offset into the monitor bitmap = physRect origin minus monitor's physical origin.
            int bx = Math.Max(0, physRect.X - monRect.Left);
            int by = Math.Max(0, physRect.Y - monRect.Top);
            int bw = Math.Max(1, Math.Min(physRect.Width,  fullBitmap.Width  - bx));
            int bh = Math.Max(1, Math.Min(physRect.Height, fullBitmap.Height - by));

            using var regionBitmap = fullBitmap.Clone(new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);

            var lines = await _ocr.RecognizeAsync(regionBitmap, _settings.SourceLanguage, ct);
            if (lines.Count == 0)
            {
                Application.Current.Dispatcher.Invoke(() => _overlay.UpdateWithTranslation([]));
                return;
            }

            foreach (var line in lines)
            {
                line.BoundingRect = Offset(line.BoundingRect, bx, by);
                foreach (var word in line.Words)
                    word.BoundingRect = Offset(word.BoundingRect, bx, by);
            }

            var blocks = await LayoutGrouping.GroupLinesAsync(lines, _layout, regionBitmap, ct, bx, by);
            var translatable = blocks.Select((b, i) => new TranslationService.TranslatableBlock(
                i, b.FullText, b.BoundingRect.X, b.BoundingRect.Y, b.BoundingRect.Width, b.BoundingRect.Height)).ToList();
            var translatedById = await _translator.TranslateBlocksAsync(translatable,
                _settings.SourceLanguage, _settings.TargetLanguage, ct);

            double monLogX = monRect.Left / dpiScale;
            double monLogY = monRect.Top  / dpiScale;

            var items = new List<OverlayTextItem>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var text = translatedById.TryGetValue(i, out var t) && !string.IsNullOrWhiteSpace(t) ? t : blocks[i].FullText;
                Console.WriteLine($"Block {i}: '{blocks[i].FullText}' → '{text}'");
                var item = OverlayTextItem.Build(blocks[i], text, fullBitmap, dpiScale);
                item.ScreenRect = new System.Windows.Rect(
                    item.ScreenRect.X + monLogX, item.ScreenRect.Y + monLogY,
                    item.ScreenRect.Width, item.ScreenRect.Height);
                items.Add(item);
            }

            _storedBitmap?.Dispose();
            _storedBitmap = new System.Drawing.Bitmap(fullBitmap);
            _storedMonRect = monRect;
            _storedDpiScale = dpiScale;

            if (_settings.ShowDebugRects)
            {
                Rect ToLog(Rect p) => new(
                    p.X / dpiScale + monLogX, p.Y / dpiScale + monLogY,
                    p.Width / dpiScale, p.Height / dpiScale);
                var lineRectsLog  = lines.Select(l => ToLog(l.BoundingRect)).ToList();
                var blockRectsLog = blocks.Select(b => ToLog(b.BoundingRect)).ToList();
                Application.Current.Dispatcher.Invoke(() =>
                    _overlay.UpdateWithTranslationAndDebug(items, lineRectsLog, blockRectsLog));
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() => _overlay.UpdateWithTranslation(items));
            }
        }
        catch
        {
            Application.Current.Dispatcher.Invoke(() => _overlay.HideOverlay());
            throw;
        }
    }

    private async Task RunTextPaneMode(System.Drawing.Rectangle physRect, CancellationToken ct)
    {
        using var regionBitmap = ScreenCaptureService.CaptureRegion(physRect);

        TextPaneWindow? pane = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            pane = new TextPaneWindow();
            _activePane = pane;
            pane.Closed += (_, _) => { if (_activePane == pane) _activePane = null; };
            pane.ShowLoading();
        });

        try
        {
            var lines = await _ocr.RecognizeAsync(regionBitmap, _settings.SourceLanguage, ct);
            if (lines.Count == 0)
            {
                Application.Current.Dispatcher.Invoke(() => pane!.ShowTranslation([]));
                return;
            }

            var blocks = await LayoutGrouping.GroupLinesAsync(lines, _layout, regionBitmap, ct);
            var translatable = blocks.Select((b, i) => new TranslationService.TranslatableBlock(
                i, b.FullText, b.BoundingRect.X, b.BoundingRect.Y, b.BoundingRect.Width, b.BoundingRect.Height)).ToList();
            var translatedById = await _translator.TranslateBlocksAsync(translatable,
                _settings.SourceLanguage, _settings.TargetLanguage, ct);
            var translated = blocks.Select((b, i) =>
                translatedById.TryGetValue(i, out var t) && !string.IsNullOrWhiteSpace(t) ? t : b.FullText).ToList();

            Application.Current.Dispatcher.Invoke(() => pane!.ShowTranslation(translated));
        }
        catch
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_activePane == pane) { pane?.Close(); _activePane = null; }
            });
            throw;
        }
    }

    public async Task AddRegionAsync(CancellationToken ct)
    {
        if (_storedBitmap is null) return;

        var tcs = new TaskCompletionSource<bool>();
        WindowPickerOverlay picker = null!;
        Application.Current.Dispatcher.Invoke(() =>
        {
            picker = new WindowPickerOverlay();
            picker.Closed += (_, _) => tcs.TrySetResult(true);
            picker.Show();
        });
        await tcs.Task;

        if (picker.Cancelled || picker.SelectedPhysRect is null) return;
        ct.ThrowIfCancellationRequested();

        var physRect = picker.SelectedPhysRect.Value;
        var fullBitmap = _storedBitmap;
        var monRect    = _storedMonRect;
        var dpiScale   = _storedDpiScale;

        int bx = Math.Max(0, physRect.X - monRect.Left);
        int by = Math.Max(0, physRect.Y - monRect.Top);
        int bw = Math.Min(physRect.Width,  fullBitmap.Width  - bx);
        int bh = Math.Min(physRect.Height, fullBitmap.Height - by);
        if (bw <= 0 || bh <= 0) return;

        using var regionBitmap = fullBitmap.Clone(
            new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);

        var lines = await _ocr.RecognizeAsync(regionBitmap, _settings.SourceLanguage, ct);
        if (lines.Count == 0) return;

        foreach (var line in lines)
        {
            line.BoundingRect = Offset(line.BoundingRect, bx, by);
            foreach (var word in line.Words)
                word.BoundingRect = Offset(word.BoundingRect, bx, by);
        }

        var blocks = await LayoutGrouping.GroupLinesAsync(lines, _layout, regionBitmap, ct, bx, by);
        var translatable = blocks.Select((b, i) => new TranslationService.TranslatableBlock(
            i, b.FullText, b.BoundingRect.X, b.BoundingRect.Y,
            b.BoundingRect.Width, b.BoundingRect.Height)).ToList();
        var translatedById = await _translator.TranslateBlocksAsync(
            translatable, _settings.SourceLanguage, _settings.TargetLanguage, ct);

        double monLogX = monRect.Left / dpiScale;
        double monLogY = monRect.Top  / dpiScale;

        var items = new List<OverlayTextItem>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var text = translatedById.TryGetValue(i, out var t) && !string.IsNullOrWhiteSpace(t)
                ? t : blocks[i].FullText;
            var item = OverlayTextItem.Build(blocks[i], text, fullBitmap, dpiScale);
            item.ScreenRect = new System.Windows.Rect(
                item.ScreenRect.X + monLogX, item.ScreenRect.Y + monLogY,
                item.ScreenRect.Width, item.ScreenRect.Height);
            items.Add(item);
        }

        Application.Current.Dispatcher.Invoke(() => _overlay.AddTranslationItems(items));
    }

    public void Deactivate()
    {
        _storedBitmap?.Dispose();
        _storedBitmap = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            _overlay.HideOverlay();
            _activePane?.Close();
            _activePane = null;
        });
    }

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
