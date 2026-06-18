using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using TransIt.Core;
using TransIt.Services;
using TransIt.Windows.Selection;
using TransIt.Windows.Chat;
using TransIt.Infrastructure;

namespace TransIt.Modes;

public class SummaryMode : ITranslationMode
{
    private readonly OcrService _ocr;
    private readonly AppSettings _settings;
    private ChatWindow? _activeChat;

    public SummaryMode(OcrService ocr, AppSettings settings)
    {
        _ocr = ocr;
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

        var lines = await _ocr.RecognizeAsync(bitmap, _settings.SourceLanguage, ct);
        if (lines.Count == 0)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var emptyChat = new ChatWindow();
                _activeChat = emptyChat;
                emptyChat.Closed += (_, _) => { if (_activeChat == emptyChat) _activeChat = null; };
                emptyChat.ShowLoading();
                emptyChat.AddSummary("(No text detected)", null!);
            });
            return;
        }

        var fullText = string.Join("\n", lines.Select(l => l.FullText));

        ChatWindow? chat = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            chat = new ChatWindow();
            _activeChat = chat;
            chat.Closed += (_, _) => { if (_activeChat == chat) _activeChat = null; };
            chat.ShowLoading();
        });

        AssistantsChatService? service = null;
        try
        {
            service = new AssistantsChatService(_settings.OpenAiApiKey, _settings.OpenAiModel);
            var summary = await service.StartTextSessionAsync(fullText, _settings.SourceLanguage, ct);
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_activeChat == chat) chat!.AddSummary(summary, service);
                else _ = service.DisposeAsync().AsTask();
            });
        }
        catch
        {
            if (service is not null) await service.DisposeAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_activeChat == chat) { chat?.Close(); _activeChat = null; }
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

        int cursorX = physRect.X + physRect.Width  / 2;
        int cursorY = physRect.Y + physRect.Height / 2;

        // Capture target HWND before overlays are shown — WindowFromPoint returns the real
        // app at that point, not any of our overlays (which don't exist yet).
        var centerPt = new NativeMethods.POINT { X = cursorX, Y = cursorY };
        IntPtr targetHwnd = NativeMethods.WindowFromPoint(centerPt);

        CaptureRegionIndicator? indicator = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            indicator = new CaptureRegionIndicator();
            indicator.ShowForRect(physRect, dpiScale);
        });

        // Phase 2: show capture bar + preview strip
        var barTcs = new TaskCompletionSource<bool>(); // true=finalize, false=cancel
        bool autoMode = false;
        var stepSem = new SemaphoreSlim(0);
        // Signals the step-wait when ct is cancelled, so the loop can exit cleanly.
        var ctDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctReg = ct.Register(() => ctDone.TrySetResult());

        ScrollCaptureBar? bar = null;
        ScrollPreviewWindow? preview = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            bar = new ScrollCaptureBar();
            bar.FinalizeRequested   += (_, _) => barTcs.TrySetResult(true);
            bar.CancelRequested     += (_, _) => barTcs.TrySetResult(false);
            bar.StepRequested       += (_, _) => stepSem.Release();
            bar.AutoScrollRequested += (_, _) => { autoMode = true; stepSem.Release(); };
            bar.Show();
            bar.PositionAboveRect(physRect, dpiScale);

            preview = new ScrollPreviewWindow();
            preview.Show();
            preview.PositionAboveRect(physRect, dpiScale, bar.Top);
        });

        // Phase 3: scroll-capture loop.
        using var stitcher = new IncrementalStitcher();
        byte[] lastStitched = [];

        // Duplicate skip threshold: frames that differ by < 5 bits are essentially identical.
        const int DuplicateThreshold = 5;

        // Bottom detection: require 3 consecutive stable ticks to avoid false positives
        // from animated scroll or slow rendering in PDF viewers.
        const int StableFramesRequired = 3;

        // Capture first frame unconditionally.
        using (var first = ScreenCaptureService.CaptureRegion(physRect))
        {
            lastStitched = stitcher.Append(BitmapToJpeg(first));
            preview?.UpdateStitched(lastStitched, stitcher.FrameCount);
        }
        Application.Current.Dispatcher.Invoke(() => bar!.UpdateCount(stitcher.FrameCount, false, "image"));

        ulong lastCapturedHash;
        using (var tmp = ScreenCaptureService.CaptureRegion(physRect))
            lastCapturedHash = ComputeHash(tmp);
        ulong prevTickHash = lastCapturedHash;
        int stableCount = 0;

        while (!barTcs.Task.IsCompleted && !ct.IsCancellationRequested)
        {
            if (!autoMode)
            {
                // Wait for Step press, Auto press, Summarize/Cancel, or ct cancel.
                await Task.WhenAny(stepSem.WaitAsync(), barTcs.Task, ctDone.Task);
                if (barTcs.Task.IsCompleted || ct.IsCancellationRequested) break;
                stableCount = 0; // explicit user action — reset bottom-detection
                if (autoMode)
                    Application.Current.Dispatcher.Invoke(() => bar!.SetStatus("Auto scrolling..."));
            }

            // Post WM_MOUSEWHEEL directly — no cursor movement, user can click Step repeatedly.
            int wp = unchecked((int)((uint)(-3 * NativeMethods.WHEEL_DELTA) << 16));
            int lp = unchecked((int)((uint)(ushort)cursorY << 16 | (uint)(ushort)cursorX));
            NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEWHEEL, new IntPtr(wp), new IntPtr(lp));
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
                    // Append final frame if it differs from last capture.
                    if (HammingDistance(lastCapturedHash, hash) >= DuplicateThreshold)
                    {
                        lastStitched = stitcher.Append(BitmapToJpeg(frame));
                        preview?.UpdateStitched(lastStitched, stitcher.FrameCount);
                        var fc = stitcher.FrameCount;
                        Application.Current.Dispatcher.Invoke(() => bar!.UpdateCount(fc, false, "image"));
                    }
                    autoMode = false;
                    Application.Current.Dispatcher.Invoke(() => bar!.SetStatus("Reached bottom — press Step to continue or Summarize"));
                }
            }
            else
            {
                stableCount = 0;
            }

            // Append every tick that has new content (skip near-identical frames only).
            if (HammingDistance(lastCapturedHash, hash) >= DuplicateThreshold)
            {
                lastStitched = stitcher.Append(BitmapToJpeg(frame));
                lastCapturedHash = hash;
                preview?.UpdateStitched(lastStitched, stitcher.FrameCount);
                var count = stitcher.FrameCount;
                Application.Current.Dispatcher.Invoke(() => bar!.UpdateCount(count, false, "image"));
            }

            prevTickHash = hash;
        }

        // Scroll stopped (bottom reached or user pressed button during scroll).
        // If user hasn't chosen yet, keep bar open and wait.
        if (!barTcs.Task.IsCompleted && !ct.IsCancellationRequested)
        {
            try { await barTcs.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { /* treat as cancel */ }
        }

        Application.Current.Dispatcher.Invoke(() => { bar!.Close(); indicator?.Close(); preview?.Close(); });

        bool finalize = barTcs.Task.IsCompleted && await barTcs.Task;
        if (lastStitched.Length == 0) return;

        // Slice stitched image and save everything to %TEMP%\TransIt\<timestamp>\ for review.
        var slices = SliceVertically(lastStitched);
        var debugDir = Path.Combine(Path.GetTempPath(), "TransIt",
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(debugDir);
        File.WriteAllBytes(Path.Combine(debugDir, "stitched.jpg"), lastStitched);
        for (int i = 0; i < slices.Count; i++)
            File.WriteAllBytes(Path.Combine(debugDir, $"slice_{i + 1:D2}.jpg"), slices[i]);
        System.Diagnostics.Process.Start("explorer.exe", debugDir);

        if (!finalize) return;

        ChatWindow? chat = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            chat = new ChatWindow();
            _activeChat = chat;
            chat.Closed += (_, _) => { if (_activeChat == chat) _activeChat = null; };
            chat.ShowLoading();
        });

        AssistantsChatService? service = null;
        try
        {
            service = new AssistantsChatService(_settings.OpenAiApiKey, _settings.OpenAiModel);
            var summary = await service.StartImageSessionAsync(slices, _settings.SourceLanguage, ct);
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_activeChat == chat) chat!.AddSummary(summary, service);
                else _ = service.DisposeAsync().AsTask();
            });
        }
        catch
        {
            if (service is not null) await service.DisposeAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_activeChat == chat) { chat?.Close(); _activeChat = null; }
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

    // Encodes bitmap as JPEG (quality 90) resized to max 1600px wide for readable text in Vision API.
    private static byte[] BitmapToJpeg(Bitmap bmp, int maxWidth = 1600)
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
                ep.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
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

    // Slices a stitched JPEG vertically into chunks of sliceHeight px with overlapRatio overlap.
    // Overlap ensures text spanning a slice boundary is readable in at least one slice.
    private static List<byte[]> SliceVertically(byte[] jpeg, int sliceHeight = 1500, double overlapRatio = 0.20)
    {
        using var ms = new MemoryStream(jpeg);
        using var src = new Bitmap(ms);

        int w    = src.Width;
        int h    = src.Height;
        int step = (int)(sliceHeight * (1.0 - overlapRatio)); // advance per slice

        var slices = new List<byte[]>();
        for (int y = 0; y < h; y += step)
        {
            int sh = Math.Min(sliceHeight, h - y);
            if (sh < 50) break; // skip meaningless trailing sliver
            using var slice = src.Clone(new System.Drawing.Rectangle(0, y, w, sh), src.PixelFormat);
            slices.Add(BitmapToJpeg(slice));
        }
        return slices;
    }

    public void Deactivate()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _activeChat?.Close();
            _activeChat = null;
        });
    }
}
