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
    private readonly TranslationService _translator;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    public SnapshotMode(OcrService ocr, TranslationService translator,
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
        await Task.Delay(150, ct);

        using var bitmap = ScreenCaptureService.CaptureFullScreen();
        double dpiScale = GetPrimaryDpiScale();

        var background = Application.Current.Dispatcher.Invoke(() => ToBitmapSource(bitmap));

        var lines = await _ocr.RecognizeAsync(bitmap, _settings.SourceLanguage, ct);
        if (lines.Count == 0)
        {
            Application.Current.Dispatcher.Invoke(() => _overlay.ShowFrozenOverlay([], background));
            return;
        }

        var blocks = OcrBlock.GroupLines(lines);
        var texts  = blocks.Select(b => b.FullText).ToList();
        var translated = await _translator.TranslateAsync(texts,
            _settings.SourceLanguage, _settings.TargetLanguage, ct);

        var items = new List<OverlayTextItem>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var text = string.IsNullOrWhiteSpace(translated[i]) ? blocks[i].FullText : translated[i];
            items.Add(OverlayTextItem.Build(blocks[i], text, bitmap));
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

    private static double GetPrimaryDpiScale() =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            var win = Application.Current.Windows.OfType<Window>().FirstOrDefault();
            if (win is null) return 1.0;
            var source = PresentationSource.FromVisual(win);
            return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        });
}
