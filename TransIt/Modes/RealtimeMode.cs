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
    private readonly TranslationService _translator;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly WinEventHook _winEventHook = new();
    private readonly ChangeDetector _changeDetector = new();

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private bool _isProcessing;
    private bool _forceNext;

    public RealtimeMode(OcrService ocr, TranslationService translator,
                        AppSettings settings, OverlayWindow overlay)
    {
        _ocr = ocr;
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
        using var bitmap = ScreenCaptureService.CaptureFullScreen();

        bool force = _forceNext;
        _forceNext = false;

        if (!force && !_changeDetector.HasChanged(bitmap)) return;

        double dpiScale = GetPrimaryDpiScale();
        var lines = await _ocr.RecognizeAsync(bitmap, _settings.SourceLanguage, ct);
        if (lines.Count == 0)
        {
            Application.Current.Dispatcher.Invoke(() => _overlay.Hide());
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
            items.Add(OverlayTextItem.Build(blocks[i], text, bitmap, dpiScale));
        }

        Application.Current.Dispatcher.Invoke(() => _overlay.ShowOverlay(items));
    }

    private static double GetPrimaryDpiScale() => DpiHelper.GetPrimaryDpiScale();
}
