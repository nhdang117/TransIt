using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
    private readonly TranslationService _translator;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private TextPaneWindow? _activePane;

    public RegionMode(OcrService ocr, TranslationService translator,
                      AppSettings settings, OverlayWindow overlay)
    {
        _ocr = ocr;
        _translator = translator;
        _settings = settings;
        _overlay = overlay;
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        Application.Current.Dispatcher.Invoke(() => _overlay.Hide());

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
        bool useVision = _settings.UseVisionApi && _settings.Provider == TranslationProvider.OpenAI;
        if (_settings.RegionOverlayMode && useVision)
            await RunVisionOverlayMode(physRect, ct);
        else if (_settings.RegionOverlayMode)
            await RunOverlayMode(physRect, ct);
        else if (useVision)
            await RunVisionMode(physRect, ct);
        else
            await RunTextPaneMode(physRect, ct);
    }

    private async Task RunOverlayMode(System.Drawing.Rectangle physRect, CancellationToken ct)
    {
        using var fullBitmap = ScreenCaptureService.CaptureFullScreen();
        double dpiScale = GetPrimaryDpiScale();
        var background = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(fullBitmap));

        Application.Current.Dispatcher.Invoke(() => _overlay.ShowLoadingOverlay(background));

        try
        {
            // Convert physRect (physical pixels) to DIP-space bitmap coords.
            // fullBitmap is DIP-sized (96 DPI, 1 px = 1 DIP), so all offsets must be in DIPs.
            int bx = Math.Max(0, (int)(physRect.X / dpiScale - SystemParameters.VirtualScreenLeft));
            int by = Math.Max(0, (int)(physRect.Y / dpiScale - SystemParameters.VirtualScreenTop));
            int bw = Math.Max(1, Math.Min((int)(physRect.Width  / dpiScale), fullBitmap.Width  - bx));
            int bh = Math.Max(1, Math.Min((int)(physRect.Height / dpiScale), fullBitmap.Height - by));

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

            var blocks = OcrBlock.GroupLines(lines);
            var texts = blocks.Select(b => b.FullText).ToList();
            var translated = await _translator.TranslateAsync(texts,
                _settings.SourceLanguage, _settings.TargetLanguage, ct);

            var items = new List<OverlayTextItem>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var text = string.IsNullOrWhiteSpace(translated[i]) ? blocks[i].FullText : translated[i];
                items.Add(OverlayTextItem.Build(blocks[i], text, fullBitmap));
            }

            Application.Current.Dispatcher.Invoke(() => _overlay.UpdateWithTranslation(items));
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

            var blocks = OcrBlock.GroupLines(lines);
            var texts = blocks.Select(b => b.FullText).ToList();
            var translated = await _translator.TranslateAsync(texts,
                _settings.SourceLanguage, _settings.TargetLanguage, ct);

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

    private async Task RunVisionOverlayMode(System.Drawing.Rectangle physRect, CancellationToken ct)
    {
        using var fullBitmap = ScreenCaptureService.CaptureFullScreen();
        double dpiScale = GetPrimaryDpiScale();
        var background = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(fullBitmap));
        Application.Current.Dispatcher.Invoke(() => _overlay.ShowLoadingOverlay(background));

        try
        {
            int bx = Math.Max(0, (int)(physRect.X / dpiScale - SystemParameters.VirtualScreenLeft));
            int by = Math.Max(0, (int)(physRect.Y / dpiScale - SystemParameters.VirtualScreenTop));
            int bw = Math.Max(1, Math.Min((int)(physRect.Width  / dpiScale), fullBitmap.Width  - bx));
            int bh = Math.Max(1, Math.Min((int)(physRect.Height / dpiScale), fullBitmap.Height - by));

            using var regionBitmap = fullBitmap.Clone(new Rectangle(bx, by, bw, bh), fullBitmap.PixelFormat);

            byte[] pngBytes;
            using (var ms = new MemoryStream())
            {
                regionBitmap.Save(ms, ImageFormat.Png);
                pngBytes = ms.ToArray();
            }

            // OCR and Vision AI run in parallel — OCR gives rects, Vision gives translations
            var ocrTask    = _ocr.RecognizeAsync(regionBitmap, _settings.SourceLanguage, ct);
            var visionTask = _translator.TranslateImageAsync(pngBytes,
                                 _settings.SourceLanguage, _settings.TargetLanguage, ct);
            await Task.WhenAll(ocrTask, visionTask);

            var lines = ocrTask.Result;
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
            var visionTexts = visionTask.Result;
            var matched     = MatchVisionToBlocks(blocks, visionTexts);

            var items = new List<OverlayTextItem>();
            for (int i = 0; i < blocks.Count; i++)
                items.Add(OverlayTextItem.Build(blocks[i], matched[i], fullBitmap));

            Application.Current.Dispatcher.Invoke(() => _overlay.UpdateWithTranslation(items));
        }
        catch
        {
            Application.Current.Dispatcher.Invoke(() => _overlay.HideOverlay());
            throw;
        }
    }

    // Zip vision paragraphs to OCR blocks by index.
    // Extra vision paragraphs append to the last block; missing ones leave block text empty.
    private static List<string> MatchVisionToBlocks(List<OcrBlock> blocks, List<string> vision)
    {
        var result = new List<string>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
            result.Add(i < vision.Count ? vision[i] : string.Empty);

        if (vision.Count > blocks.Count && blocks.Count > 0)
            result[^1] += "\n" + string.Join("\n", vision.Skip(blocks.Count));

        return result;
    }

    private async Task RunVisionMode(System.Drawing.Rectangle physRect, CancellationToken ct)
    {
        using var regionBitmap = ScreenCaptureService.CaptureRegion(physRect);

        byte[] pngBytes;
        using (var ms = new MemoryStream())
        {
            regionBitmap.Save(ms, ImageFormat.Png);
            pngBytes = ms.ToArray();
        }

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
            var translated = await _translator.TranslateImageAsync(pngBytes,
                _settings.SourceLanguage, _settings.TargetLanguage, ct);

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

    public void Deactivate()
    {
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

    private static double GetPrimaryDpiScale() =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            var win = Application.Current.Windows.OfType<Window>().FirstOrDefault();
            if (win is null) return 1.0;
            var source = PresentationSource.FromVisual(win);
            return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        });
}
