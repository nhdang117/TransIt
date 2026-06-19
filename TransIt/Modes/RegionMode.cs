using System.Drawing;
using System.Text;
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
    private readonly OcrService              _ocr;
    private readonly YoloLayoutService       _yolo;
    private readonly TableTransformerService _tableTransformer;
    private readonly TranslationService      _translator;
    private readonly AppSettings             _settings;
    private readonly OverlayWindow           _overlay;
    private TextPaneWindow?                  _activePane;

    private System.Drawing.Bitmap?     _storedBitmap;
    private System.Drawing.Rectangle   _storedMonRect;
    private double                     _storedDpiScale;

    public RegionMode(OcrService ocr, YoloLayoutService yolo,
                      TableTransformerService tableTransformer,
                      TranslationService translator, AppSettings settings, OverlayWindow overlay)
    {
        _ocr              = ocr;
        _yolo             = yolo;
        _tableTransformer = tableTransformer;
        _translator       = translator;
        _settings         = settings;
        _overlay          = overlay;
    }

    // ── Phase 1 & 2: pre-capture → picker overlay ────────────────────────────────────────
    public async Task ActivateAsync(CancellationToken ct)
    {
        Application.Current.Dispatcher.Invoke(() => _overlay.Hide());

        // Capture full monitor at cursor BEFORE any overlay window appears.
        var (monRect, dpiScale) = DpiHelper.GetMonitorAtCursor();
        var fullBitmap = ScreenCaptureService.CaptureRegion(monRect);

        try
        {
            // Start YOLO immediately — runs in parallel with ToBitmapSource + window init.
            // Clone so YOLO's background thread and ToBitmapSource (UI thread) don't share the same GDI+ bitmap.
            var layoutCts  = new CancellationTokenSource();
            var layoutTask = _yolo.DetectOwnedAsync((Bitmap)fullBitmap.Clone(), layoutCts.Token);

            var bitmapSource = Application.Current.Dispatcher.Invoke(
                () => ToBitmapSource(fullBitmap));

            LayoutPickerOverlay picker = null!;
            var tcs = new TaskCompletionSource<bool>();
            Application.Current.Dispatcher.Invoke(() =>
            {
                picker = new LayoutPickerOverlay(bitmapSource, fullBitmap, monRect, dpiScale, layoutTask, layoutCts);
                picker.Closed += (_, _) => tcs.TrySetResult(true);
                picker.Show();
            });
            await tcs.Task;

            if (picker.Cancelled || picker.SelectedPhysRect is null) return;
            ct.ThrowIfCancellationRequested();

            var physRect = picker.SelectedPhysRect.Value;
            var category = picker.SelectedCategory;

            // Manual drag → run YOLO on the selection crop to determine type.
            if (category is null)
                category = await DetectCategoryAsync(fullBitmap, physRect, monRect, ct);

            // ── Phase 3-5: branch on detected category ────────────────────────────────
            if (category == LayoutCategory.Table)
                await RunTableMode(physRect, fullBitmap, bitmapSource, monRect, dpiScale, ct);
            else if (_settings.RegionOverlayMode)
                await RunOverlayMode(physRect, fullBitmap, bitmapSource, monRect, dpiScale, ct);
            else
                await RunTextPaneMode(physRect, fullBitmap, monRect, ct);
        }
        finally
        {
            fullBitmap.Dispose();
        }
    }

    // Runs YOLO on the selected crop; returns the dominant region category or null.
    private async Task<LayoutCategory?> DetectCategoryAsync(
        System.Drawing.Bitmap fullBitmap, System.Drawing.Rectangle physRect,
        System.Drawing.Rectangle monRect, CancellationToken ct)
    {
        int bx = Math.Max(0, physRect.X - monRect.Left);
        int by = Math.Max(0, physRect.Y - monRect.Top);
        int bw = Math.Max(1, Math.Min(physRect.Width,  fullBitmap.Width  - bx));
        int bh = Math.Max(1, Math.Min(physRect.Height, fullBitmap.Height - by));

        try
        {
            using var crop = fullBitmap.Clone(new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);
            var regions = await _yolo.DetectAsync(crop, ct);
            return regions
                .OrderByDescending(r => r.BoundingRect.Width * r.BoundingRect.Height)
                .Select(r => (LayoutCategory?)r.Category)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    // ── Text / overlay path ───────────────────────────────────────────────────────────────
    private async Task RunOverlayMode(
        System.Drawing.Rectangle physRect,
        System.Drawing.Bitmap fullBitmap,
        BitmapSource bitmapSource,
        System.Drawing.Rectangle monRect,
        double dpiScale,
        CancellationToken ct)
    {
        Application.Current.Dispatcher.Invoke(() => _overlay.ShowLoadingOverlay(bitmapSource));

        try
        {
            int bx = Math.Max(0, physRect.X - monRect.Left);
            int by = Math.Max(0, physRect.Y - monRect.Top);
            int bw = Math.Max(1, Math.Min(physRect.Width,  fullBitmap.Width  - bx));
            int bh = Math.Max(1, Math.Min(physRect.Height, fullBitmap.Height - by));

            using var regionBitmap = fullBitmap.Clone(
                new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);

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

            var blocks      = OcrBlock.GroupLines(lines);
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

            _storedBitmap?.Dispose();
            _storedBitmap   = new System.Drawing.Bitmap(fullBitmap);
            _storedMonRect  = monRect;
            _storedDpiScale = dpiScale;

            if (_settings.ShowDebugRects)
            {
                System.Windows.Rect ToLog(System.Windows.Rect p) => new(
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

    private async Task RunTextPaneMode(
        System.Drawing.Rectangle physRect,
        System.Drawing.Bitmap fullBitmap,
        System.Drawing.Rectangle monRect,
        CancellationToken ct)
    {
        int bx = Math.Max(0, physRect.X - monRect.Left);
        int by = Math.Max(0, physRect.Y - monRect.Top);
        int bw = Math.Max(1, Math.Min(physRect.Width,  fullBitmap.Width  - bx));
        int bh = Math.Max(1, Math.Min(physRect.Height, fullBitmap.Height - by));

        using var regionBitmap = fullBitmap.Clone(new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);

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

            var blocks      = OcrBlock.GroupLines(lines);
            var translatable = blocks.Select((b, i) => new TranslationService.TranslatableBlock(
                i, b.FullText, b.BoundingRect.X, b.BoundingRect.Y,
                b.BoundingRect.Width, b.BoundingRect.Height)).ToList();
            var translatedById = await _translator.TranslateBlocksAsync(
                translatable, _settings.SourceLanguage, _settings.TargetLanguage, ct);
            var translated = blocks.Select((b, i) =>
                translatedById.TryGetValue(i, out var t) && !string.IsNullOrWhiteSpace(t)
                    ? t : b.FullText).ToList();

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

    // ── Table path ────────────────────────────────────────────────────────────────────────
    private async Task RunTableMode(
        System.Drawing.Rectangle physRect,
        System.Drawing.Bitmap fullBitmap,
        BitmapSource bitmapSource,
        System.Drawing.Rectangle monRect,
        double dpiScale,
        CancellationToken ct)
    {
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
            int bx = Math.Max(0, physRect.X - monRect.Left);
            int by = Math.Max(0, physRect.Y - monRect.Top);
            int bw = Math.Max(1, Math.Min(physRect.Width,  fullBitmap.Width  - bx));
            int bh = Math.Max(1, Math.Min(physRect.Height, fullBitmap.Height - by));

            using var tableBitmap = fullBitmap.Clone(
                new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);

            // Step 1: extract cell grid via Table Transformer.
            var cells = await _tableTransformer.ExtractCellsAsync(tableBitmap, ct);

            // Step 2: OCR each cell — RapidOCR for speed (text-only; bbox already known from TATR).
            foreach (var cell in cells)
            {
                ct.ThrowIfCancellationRequested();
                int cx = Math.Max(0, (int)cell.BoundingRect.X);
                int cy = Math.Max(0, (int)cell.BoundingRect.Y);
                int cw = Math.Max(1, Math.Min((int)cell.BoundingRect.Width,  tableBitmap.Width  - cx));
                int ch = Math.Max(1, Math.Min((int)cell.BoundingRect.Height, tableBitmap.Height - cy));

                using var cellBitmap = tableBitmap.Clone(
                    new Rectangle(cx, cy, cw, ch), tableBitmap.PixelFormat);
                var cellLines = await _ocr.RecognizeAsync(cellBitmap, _settings.SourceLanguage, ct);
                cell.Text = string.Join(" ", cellLines.Select(l => l.FullText)).Trim();
            }

            // Step 3: format as markdown table.
            string markdownTable = BuildMarkdownTable(cells);
            if (string.IsNullOrWhiteSpace(markdownTable))
            {
                Application.Current.Dispatcher.Invoke(() => pane!.ShowTranslation([]));
                return;
            }

            // Step 4: translate the whole table as one block.
            var translatable = new List<TranslationService.TranslatableBlock>
            {
                new(0, markdownTable, physRect.X, physRect.Y, physRect.Width, physRect.Height),
            };
            var translatedById = await _translator.TranslateBlocksAsync(
                translatable, _settings.SourceLanguage, _settings.TargetLanguage, ct);

            string translated = translatedById.TryGetValue(0, out var tv) && !string.IsNullOrWhiteSpace(tv)
                ? tv : markdownTable;

            Application.Current.Dispatcher.Invoke(() => pane!.ShowTranslation([translated]));
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

    private static string BuildMarkdownTable(List<TableCell> cells)
    {
        if (cells.Count == 0) return "";

        int rows = cells.Max(c => c.Row) + 1;
        int cols = cells.Max(c => c.Col) + 1;

        var grid = new string[rows, cols];
        foreach (var cell in cells)
            if (cell.Row < rows && cell.Col < cols)
                grid[cell.Row, cell.Col] = cell.Text;

        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            sb.Append('|');
            for (int c = 0; c < cols; c++)
                sb.Append(' ').Append(grid[r, c] ?? "").Append(" |");
            sb.AppendLine();

            // Header separator after first row.
            if (r == 0)
            {
                sb.Append('|');
                for (int c = 0; c < cols; c++) sb.Append("---|");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    // ── AddRegion (re-use stored bitmap) ─────────────────────────────────────────────────
    public async Task AddRegionAsync(CancellationToken ct)
    {
        if (_storedBitmap is null) return;

        var fullBitmap = _storedBitmap;
        var monRect    = _storedMonRect;
        var dpiScale   = _storedDpiScale;

        // Start YOLO immediately — runs in parallel with ToBitmapSource + window init.
        var layoutCts2  = new CancellationTokenSource();
        var layoutTask2 = _yolo.DetectOwnedAsync((Bitmap)fullBitmap.Clone(), layoutCts2.Token);

        var bitmapSource = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(fullBitmap));

        LayoutPickerOverlay picker = null!;
        var tcs = new TaskCompletionSource<bool>();
        Application.Current.Dispatcher.Invoke(() =>
        {
            picker = new LayoutPickerOverlay(bitmapSource, fullBitmap, monRect, dpiScale, layoutTask2, layoutCts2);
            picker.Closed += (_, _) => tcs.TrySetResult(true);
            picker.Show();
        });
        await tcs.Task;

        if (picker.Cancelled || picker.SelectedPhysRect is null) return;
        ct.ThrowIfCancellationRequested();

        var physRect = picker.SelectedPhysRect.Value;
        if (picker.SelectedCategory == null)
            _ = await DetectCategoryAsync(fullBitmap, physRect, monRect, ct);

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

        var blocks      = OcrBlock.GroupLines(lines);
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
        finally { NativeMethods.DeleteObject(hbmp); }
    }
}
