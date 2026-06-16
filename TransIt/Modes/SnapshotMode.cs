using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TransIt.Core;
using TransIt.Infrastructure;
using TransIt.Models;
using TransIt.Windows.Overlay;

namespace TransIt.Modes;

public class SnapshotMode : ITranslationMode
{
    private readonly OcrService _ocr;
    private readonly LayoutService _layout;
    private readonly TranslationService _translator;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    public SnapshotMode(OcrService ocr, LayoutService layout, TranslationService translator,
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
        await Task.Delay(150, ct);

        var capture = ScreenCaptureService.CaptureMonitorAtCursor();
        using var bitmap = capture.bitmap;
        double dpiScale = capture.dpiScale;
        var monRect = capture.monRect;

        var background = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(bitmap));

        var lines = await _ocr.RecognizeAsync(bitmap, _settings.SourceLanguage, ct);
        if (lines.Count == 0)
        {
            Application.Current.Dispatcher.Invoke(() => _overlay.ShowFrozenOverlay([], background));
            return;
        }

        var blocks = await LayoutGrouping.GroupLinesAsync(lines, _layout, bitmap, ct);
        var translatable = blocks.Select((b, i) => new TranslationService.TranslatableBlock(
            i, b.FullText, b.BoundingRect.X, b.BoundingRect.Y, b.BoundingRect.Width, b.BoundingRect.Height)).ToList();
        var translatedById = await _translator.TranslateBlocksAsync(translatable,
            _settings.SourceLanguage, _settings.TargetLanguage, ct);

        // Monitor-relative DIPs → virtual-screen DIPs so the canvas (spanning all monitors) positions items correctly.
        double monLogX = monRect.Left / dpiScale;
        double monLogY = monRect.Top  / dpiScale;

        var items = new List<OverlayTextItem>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var text = translatedById.TryGetValue(i, out var t) && !string.IsNullOrWhiteSpace(t) ? t : blocks[i].FullText;
            var item = OverlayTextItem.Build(blocks[i], text, bitmap, dpiScale);
            item.ScreenRect = new System.Windows.Rect(
                item.ScreenRect.X + monLogX, item.ScreenRect.Y + monLogY,
                item.ScreenRect.Width, item.ScreenRect.Height);
            items.Add(item);
        }

        Application.Current.Dispatcher.Invoke(() => _overlay.ShowFrozenOverlay(items, background));
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        IntPtr hbmp = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hbmp, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            // Force 96 DPI so 1 image pixel = 1 WPF DIP, aligning with OCR coordinate space
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

    public void Deactivate() =>
        Application.Current.Dispatcher.Invoke(() => _overlay.Hide());

}
