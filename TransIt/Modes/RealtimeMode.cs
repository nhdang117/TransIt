using System.Windows;
using TransIt.Core;
using TransIt.Infrastructure;
using TransIt.Models;
using TransIt.Services;
using TransIt.Windows.Overlay;

namespace TransIt.Modes;

public class RealtimeMode : ITranslationMode
{
    private readonly OcrService _ocr;
    private readonly LayoutService _layout;
    private readonly TranslationService _translator;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly WinEventHook _winEventHook = new();
    private readonly ChangeDetector _changeDetector = new();

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private bool _isProcessing;
    private bool _forceNext;

    public RealtimeMode(OcrService ocr, LayoutService layout, TranslationService translator,
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
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _changeDetector.Reset();
        _forceNext = true;

        _winEventHook.AppSwitched += OnAppSwitched;
        _winEventHook.Start();

        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.RealtimeIntervalMs));

        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                if (_isProcessing) continue;
                _isProcessing = true;
                try { await RunPipelineAsync(_cts.Token); }
                catch (OperationCanceledException) { break; }
                catch { /* swallow per-tick errors */ }
                finally { _isProcessing = false; }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Deactivate()
    {
        _cts?.Cancel();
        _winEventHook.AppSwitched -= OnAppSwitched;
        _winEventHook.Stop();
        _timer?.Dispose();
        Application.Current.Dispatcher.Invoke(() => _overlay.Hide());
    }

    private void OnAppSwitched(object? sender, EventArgs e) => _forceNext = true;

    private async Task RunPipelineAsync(CancellationToken ct)
    {
        var capture = ScreenCaptureService.CaptureMonitorAtCursor();
        using var bitmap = capture.bitmap;
        double dpiScale = capture.dpiScale;
        var monRect = capture.monRect;

        bool force = _forceNext;
        _forceNext = false;

        if (!force && !_changeDetector.HasChanged(bitmap)) return;
        var lines = await _ocr.RecognizeAsync(bitmap, _settings.SourceLanguage, ct);
        if (lines.Count == 0)
        {
            Application.Current.Dispatcher.Invoke(() => _overlay.Hide());
            return;
        }

        var blocks = await LayoutGrouping.GroupLinesAsync(lines, _layout, bitmap, ct);
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
            var item = OverlayTextItem.Build(blocks[i], text, bitmap, dpiScale);
            item.ScreenRect = new System.Windows.Rect(
                item.ScreenRect.X + monLogX, item.ScreenRect.Y + monLogY,
                item.ScreenRect.Width, item.ScreenRect.Height);
            items.Add(item);
        }

        Application.Current.Dispatcher.Invoke(() => _overlay.ShowOverlay(items));
    }

}
