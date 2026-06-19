using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using TransIt.Models;

namespace TransIt.Core;

// Layout detection runs out-of-process. paddle_inference_c.dll (used here) and the
// RapidOCR runtime both ship same-named native DLLs (mkldnn.dll, mklml.dll,
// libiomp5md.dll) but different, incompatible builds — Windows keys its module table by
// base filename, so whichever loads first in a process wins that name for every other
// consumer too. OcrService always runs before this in every mode, so in-process layout
// detection always got OCR's incompatible copies and failed to load. Running the detector
// in its own child process (TransIt.LayoutWorker) gives it an isolated module table.
public class LayoutService : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _worker;
    private bool _disposed;

    // Starts the worker process and runs a throwaway inference so MKL-DNN graph optimization
    // (fixed 800x608 picodet input) is paid at app startup rather than on the first real request.
    public async Task WarmUpAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var tiny = new Bitmap(32, 32);
            await DetectAsync(tiny, cts.Token);
            Debug.WriteLine("[LAYOUT] warmup done");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LAYOUT] warmup failed: {ex.Message}");
        }
    }

    public async Task<List<LayoutRegion>> DetectAsync(Bitmap bitmap, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var swTotal = Stopwatch.StartNew();

        var sw = Stopwatch.StartNew();
        byte[] pngBytes;
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            pngBytes = ms.ToArray();
        }
        Debug.WriteLine($"[LAYOUT] png_encode  = {sw.ElapsedMilliseconds} ms  ({pngBytes.Length / 1024} KB)");

        await _lock.WaitAsync(ct);
        try
        {
            Process worker = GetOrStartWorker();

            sw.Restart();
            await WriteFrameAsync(worker.StandardInput.BaseStream, pngBytes, ct);
            byte[] responseBytes = await ReadFrameAsync(worker.StandardOutput.BaseStream, ct)
                ?? throw new IOException("Layout worker closed its output stream unexpectedly.");
            Debug.WriteLine($"[LAYOUT] worker_rtt  = {sw.ElapsedMilliseconds} ms  ({responseBytes.Length} bytes)");

            var response = JsonSerializer.Deserialize<WorkerResponse>(responseBytes)
                ?? throw new IOException("Layout worker returned an empty response.");

            if (!response.ok)
                throw new IOException($"Layout worker error: {response.error}");

            var result = (response.regions ?? [])
                .Select(r => new LayoutRegion
                {
                    Category = Enum.Parse<LayoutCategory>(r.category),
                    BoundingRect = new System.Windows.Rect(r.x, r.y, r.w, r.h),
                    Confidence = r.confidence,
                })
                .ToList();

            Debug.WriteLine($"[LAYOUT] total       = {swTotal.ElapsedMilliseconds} ms  ({result.Count} regions)");
            return result;
        }
        catch
        {
            // The worker's stdin/stdout framing is stateful - any failure mid-exchange
            // (timeout, malformed frame, crash) leaves it desynced, so don't reuse it.
            KillWorker();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private Process GetOrStartWorker()
    {
        if (_worker != null && !_worker.HasExited) return _worker;

        string workerExe = Path.Combine(AppContext.BaseDirectory, "LayoutWorker", "TransIt.LayoutWorker.exe");
        string modelDir = Path.Combine(AppContext.BaseDirectory,
            "Assets", "LayoutModel", "picodet_lcnet_x1_0_fgd_layout_infer");

        var psi = new ProcessStartInfo
        {
            FileName = workerExe,
            ArgumentList = { modelDir },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _worker = Process.Start(psi) ?? throw new IOException($"Failed to start layout worker at {workerExe}.");

        // PaddleInference's native logging (glog) writes straight to stderr. If nobody drains
        // that pipe, it fills its OS buffer and the worker blocks on its own log write mid-
        // request - confirmed via repro: an unread stderr deadlocked the worker on a single
        // small image (88 log lines was enough). stdout carries only the framed JSON protocol
        // and stays clean, so only stderr needs a standing reader.
        _ = Task.Run(async () =>
        {
            try { while (await _worker.StandardError.ReadLineAsync() != null) { } }
            catch { /* worker exited; nothing to drain */ }
        });

        return _worker;
    }

    private void KillWorker()
    {
        try { if (_worker != null && !_worker.HasExited) _worker.Kill(); }
        catch { /* best effort */ }
        _worker?.Dispose();
        _worker = null;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);
        await stream.WriteAsync(lenBuf, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        if (!await ReadExactAsync(stream, lenBuf, ct)) return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (length < 0) return null;

        byte[] buf = new byte[length];
        if (!await ReadExactAsync(stream, buf, ct)) return null;
        return buf;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    private record WorkerRegion(string category, int x, int y, int w, int h, float confidence);
    private record WorkerResponse(bool ok, List<WorkerRegion>? regions, string? error);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KillWorker();
        _lock.Dispose();
    }
}
