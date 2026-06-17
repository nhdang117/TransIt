using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using TransIt.Core;
using TransIt.Services;
using TransIt.Windows.Selection;
using TransIt.Windows.TextPane;
using TransIt.Infrastructure;

namespace TransIt.Modes;

public class SummaryMode : ITranslationMode
{
    private readonly OcrService _ocr;
    private readonly TranslationService _translator;
    private readonly AppSettings _settings;
    private TextPaneWindow? _activePane;

    public SummaryMode(OcrService ocr, TranslationService translator, AppSettings settings)
    {
        _ocr = ocr;
        _translator = translator;
        _settings = settings;
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
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
        using var bitmap = ScreenCaptureService.CaptureRegion(physRect);

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
            var lines = await _ocr.RecognizeAsync(bitmap, _settings.SourceLanguage, ct);
            if (lines.Count == 0)
            {
                Application.Current.Dispatcher.Invoke(() => pane!.ShowTranslation(["(No text detected)"]));
                return;
            }

            var fullText = string.Join("\n", lines.Select(l => l.FullText));
            var summary = await _translator.SummarizeAsync(fullText, _settings.SourceLanguage, ct);

            Application.Current.Dispatcher.Invoke(() => pane!.ShowTranslation([summary]));
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

    public async Task ActivateScrollAsync(CancellationToken ct)
    {
        // Phase 1: region selection
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
        var dpiScale = picker.SelectedDpiScale;

        CaptureRegionIndicator? indicator = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            indicator = new CaptureRegionIndicator();
            indicator.ShowForRect(physRect, dpiScale);
        });

        // Phase 2: show capture bar + preview strip
        var barTcs = new TaskCompletionSource<bool>(); // true=finalize, false=cancel
        ScrollCaptureBar? bar = null;
        ScrollPreviewWindow? preview = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            bar = new ScrollCaptureBar();
            bar.FinalizeRequested += (_, _) => barTcs.TrySetResult(true);
            bar.CancelRequested   += (_, _) => barTcs.TrySetResult(false);
            bar.Show();

            preview = new ScrollPreviewWindow();
            preview.Show();
        });

        // Phase 3: auto-scroll with hash-triggered capture.
        // Scroll 1 notch at a time (50ms each). Capture only when Hamming distance from
        // the last captured frame exceeds the overlap threshold — this fires naturally when
        // ~80% of the viewport has scrolled, giving ~20% overlap without SampleFrames.
        // Bottom detection: hash barely changes between consecutive ticks.
        var capturedFrames = new List<byte[]>();

        int cursorX = physRect.X + physRect.Width  / 2;
        int cursorY = physRect.Y + physRect.Height / 2;

        // Pre-build 3-notch scroll input (reused every tick).
        // 3 notches per tick ensures PDF viewers move enough for the 16×16 hash to detect change.
        var scrollInput = new NativeMethods.INPUT[1];
        scrollInput[0].type = NativeMethods.INPUT_MOUSE;
        scrollInput[0].mi.dwFlags = NativeMethods.MOUSEEVENTF_WHEEL;
        scrollInput[0].mi.mouseData = unchecked((uint)(-3 * NativeMethods.WHEEL_DELTA));
        int inputSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>();

        // Duplicate skip threshold: frames that differ by < 5 bits are essentially identical.
        const int DuplicateThreshold = 5;

        // Bottom detection: require 3 consecutive stable ticks to avoid false positives
        // from animated scroll or slow rendering in PDF viewers.
        const int StableFramesRequired = 3;

        // Capture first frame unconditionally.
        using (var first = ScreenCaptureService.CaptureRegion(physRect))
        {
            capturedFrames.Add(BitmapToJpeg(first));
            preview?.AddFrame(capturedFrames[^1]);
        }
        Application.Current.Dispatcher.Invoke(() => bar!.UpdateCount(1, false, "image"));

        ulong lastCapturedHash;
        using (var tmp = ScreenCaptureService.CaptureRegion(physRect))
            lastCapturedHash = ComputeHash(tmp);
        ulong prevTickHash = lastCapturedHash;
        int stableCount = 0;

        while (!barTcs.Task.IsCompleted && !ct.IsCancellationRequested)
        {
            // Scroll 3 notches.
            NativeMethods.SetCursorPos(cursorX, cursorY);
            NativeMethods.SendInput(1, scrollInput, inputSize);

            // 200ms gives PDF viewers (Foxit, Acrobat) time to finish animated scroll + render.
            try { await Task.Delay(200, ct); }
            catch { break; }

            using var frame = ScreenCaptureService.CaptureRegion(physRect);
            ulong hash = ComputeHash(frame);

            // Bottom detection: content not moving — require multiple consecutive stable frames.
            if (HammingDistance(prevTickHash, hash) < DuplicateThreshold)
            {
                stableCount++;
                if (stableCount >= StableFramesRequired)
                {
                    // Save final frame if it differs from last capture.
                    if (HammingDistance(lastCapturedHash, hash) >= DuplicateThreshold)
                    {
                        capturedFrames.Add(BitmapToJpeg(frame));
                        preview?.AddFrame(capturedFrames[^1]);
                        var fc = capturedFrames.Count;
                        Application.Current.Dispatcher.Invoke(() => bar!.UpdateCount(fc, false, "image"));
                    }
                    barTcs.TrySetResult(true); // auto-finalize on reaching bottom
                    break;
                }
            }
            else
            {
                stableCount = 0;
            }

            // Capture every tick that has new content (skip near-identical frames only).
            // SampleFrames handles the Vision API 20-image cap later.
            if (HammingDistance(lastCapturedHash, hash) >= DuplicateThreshold)
            {
                capturedFrames.Add(BitmapToJpeg(frame));
                lastCapturedHash = hash;
                preview?.AddFrame(capturedFrames[^1]);
                var count = capturedFrames.Count;
                Application.Current.Dispatcher.Invoke(() => bar!.UpdateCount(count, false, "image"));
            }

            prevTickHash = hash;
        }

        Application.Current.Dispatcher.Invoke(() => { bar!.Close(); indicator?.Close(); preview?.Close(); });

        // Save captured frames to %TEMP%\TransIt\<timestamp>\ for review.
        var debugDir = Path.Combine(Path.GetTempPath(), "TransIt",
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(debugDir);
        for (int i = 0; i < capturedFrames.Count; i++)
            File.WriteAllBytes(Path.Combine(debugDir, $"frame_{i + 1:D3}.jpg"), capturedFrames[i]);
        System.Diagnostics.Process.Start("explorer.exe", debugDir);

        bool finalize = barTcs.Task.IsCompleted && await barTcs.Task;
        if (capturedFrames.Count == 0) return;

        // Phase 4: stitch all frames into one tall image.
        var stitched = await Task.Run(() => ImageStitcher.StitchVertically(capturedFrames), ct);
        File.WriteAllBytes(Path.Combine(debugDir, "stitched.jpg"), stitched);

        if (!finalize) return;

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
            var summary = await _translator.SummarizeImagesAsync([stitched], _settings.SourceLanguage, ct);
            Application.Current.Dispatcher.Invoke(() => pane!.ShowTranslation([summary]));
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

    // ── Scroll capture helpers ────────────────────────────────────────────────

    // 64-bit average perceptual hash of the full frame (scaled to 16×16 grayscale).
    private static ulong ComputeHash(Bitmap bmp)
    {
        using var small = new Bitmap(bmp, new System.Drawing.Size(16, 16));
        long total = 0;
        int[] pixels = new int[256];
        for (int row = 0; row < 16; row++)
            for (int col = 0; col < 16; col++)
            {
                var c = small.GetPixel(col, row);
                int gray = (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
                pixels[row * 16 + col] = gray;
                total += gray;
            }
        long avg = total / 256;
        // Sample evenly across all 256 pixels (every 4th), so the hash represents
        // the full frame rather than only the top 4 rows.
        ulong hash = 0;
        for (int i = 0; i < 64; i++)
            if (pixels[i * 4] >= avg) hash |= 1UL << i;
        return hash;
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        ulong x = a ^ b;
        int n = 0;
        while (x != 0) { n += (int)(x & 1); x >>= 1; }
        return n;
    }

    // Encodes bitmap as JPEG (quality 75) resized to max 800px wide to limit Vision API token cost.
    private static byte[] BitmapToJpeg(Bitmap bmp, int maxWidth = 800)
    {
        Bitmap? resized = null;
        try
        {
            if (bmp.Width > maxWidth)
                resized = new Bitmap(bmp, new System.Drawing.Size(maxWidth, (int)((double)bmp.Height * maxWidth / bmp.Width)));

            var target = resized ?? bmp;
            var encoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ms = new MemoryStream();
            if (encoder != null)
            {
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, 75L);
                target.Save(ms, encoder, ep);
            }
            else
            {
                target.Save(ms, ImageFormat.Jpeg);
            }
            return ms.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }
    }

    public void Deactivate()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _activePane?.Close();
            _activePane = null;
        });
    }
}
