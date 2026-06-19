using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using TransIt.Core;
using TransIt.Modes;
using TransIt.Windows.Overlay;
using TransIt.Windows.Settings;
using TransIt.Windows.TrayIcon;

namespace TransIt;

public partial class App : Application
{
    private AppSettings _settings = null!;
    private HotkeyManager _hotkeys = null!;
    private TrayIconManager _tray = null!;
    private OverlayWindow _overlay = null!;
    private OcrService _ocr = null!;
    private LayoutService _layout = null!;
    private TranslationService _translator = null!;

    private RegionMode?   _regionMode;
    private SummaryMode?  _summaryMode;
    private OcrDebugMode? _ocrDebugMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        //try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        _settings   = AppSettings.Load();
        _ocr        = new OcrService();
        _layout     = new LayoutService();
        _translator = new TranslationService(_settings);

        // Pre-warm both engines in background — pays MKL-DNN graph-optimization
        // cost before the first user hotkey, not during it.
        _ = _ocr.WarmUpAsync();
        _ = _layout.WarmUpAsync();
        _overlay    = new OverlayWindow();

        _regionMode   = new RegionMode(_ocr, _layout, _translator, _settings, _overlay);
        _summaryMode  = new SummaryMode(_ocr, _settings);
        _ocrDebugMode = new OcrDebugMode(_ocr, _layout, _settings, _overlay);

        _overlay.AddRegionRequested += (_, _) => RunAddRegion();

        _tray = new TrayIconManager();
        _tray.RegionRequested   += (_, _) => RunRegion();
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.Initialize();

        _hotkeys = new HotkeyManager();
        _hotkeys.Initialize();
        _hotkeys.HotkeyPressed += OnHotkey;

        bool ctrl2       = _hotkeys.Register(HotkeyManager.ID_REGION,         Infrastructure.NativeMethods.MOD_CONTROL, Infrastructure.NativeMethods.VK_2);
        bool ctrl3       = _hotkeys.Register(HotkeyManager.ID_SUMMARY,        Infrastructure.NativeMethods.MOD_CONTROL, Infrastructure.NativeMethods.VK_3);
        bool ctrl1       = _hotkeys.Register(HotkeyManager.ID_OCR_DEBUG,      Infrastructure.NativeMethods.MOD_CONTROL, Infrastructure.NativeMethods.VK_1);
        bool ctrlShift3  = _hotkeys.Register(HotkeyManager.ID_SUMMARY_SCROLL, Infrastructure.NativeMethods.MOD_CONTROL | Infrastructure.NativeMethods.MOD_SHIFT, Infrastructure.NativeMethods.VK_3);

        if (!ctrl2 || !ctrl3 || !ctrl1 || !ctrlShift3)
            _tray.ShowBalloon("TransIt", "Some hotkeys could not be registered (already in use).", BalloonIcon.Warning);

        // First-run: open settings if no API key configured
        if (!_settings.HasValidApiKey)
            OpenSettings();
    }

    private void OnHotkey(object? sender, int id)
    {
        switch (id)
        {
            case HotkeyManager.ID_REGION:         RunRegion();        break;
            case HotkeyManager.ID_SUMMARY:        RunSummary();       break;
            case HotkeyManager.ID_OCR_DEBUG:      RunOcrDebug();      break;
            case HotkeyManager.ID_SUMMARY_SCROLL: RunScrollSummary(); break;
        }
    }

    private void RunRegion()
    {
        if (!CheckApiKey()) return;
        Task.Run(() => _regionMode!.ActivateAsync(CancellationToken.None))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Dispatcher.Invoke(() =>
                        _tray.ShowBalloon("TransIt Error", t.Exception?.InnerException?.Message ?? "Unknown error", BalloonIcon.Error));
            });
    }

    private void RunSummary()
    {
        if (!CheckApiKey()) return;
        Task.Run(() => _summaryMode!.ActivateAsync(CancellationToken.None))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Dispatcher.Invoke(() =>
                        _tray.ShowBalloon("TransIt Error", t.Exception?.InnerException?.Message ?? "Unknown error", BalloonIcon.Error));
            });
    }

    private void RunScrollSummary()
    {
        if (!CheckApiKey()) return;
        Task.Run(() => _summaryMode!.ActivateScrollAsync(CancellationToken.None))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Dispatcher.Invoke(() =>
                        _tray.ShowBalloon("TransIt Error", t.Exception?.InnerException?.Message ?? "Unknown error", BalloonIcon.Error));
            });
    }

    private void RunAddRegion()
    {
        if (!CheckApiKey()) return;
        Task.Run(() => _regionMode!.AddRegionAsync(CancellationToken.None))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Dispatcher.Invoke(() =>
                        _tray.ShowBalloon("TransIt Error", t.Exception?.InnerException?.Message ?? "Unknown error", BalloonIcon.Error));
            });
    }

    private void RunOcrDebug()
    {
        // No API key needed — this mode only runs OCR, no translation call.
        Task.Run(() => _ocrDebugMode!.ActivateAsync(CancellationToken.None))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Dispatcher.Invoke(() =>
                        _tray.ShowBalloon("TransIt Error", t.Exception?.InnerException?.Message ?? "Unknown error", BalloonIcon.Error));
            });
    }

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings);
        win.ShowDialog();
        // Recreate translator with updated settings
        _translator   = new TranslationService(_settings);
        _regionMode   = new RegionMode(_ocr, _layout, _translator, _settings, _overlay);
        _summaryMode  = new SummaryMode(_ocr, _settings);
        _ocrDebugMode = new OcrDebugMode(_ocr, _layout, _settings, _overlay);
    }

    private bool CheckApiKey()
    {
        if (_settings.HasValidApiKey) return true;
        _tray.ShowBalloon("TransIt", "Please configure an API key in Settings.", BalloonIcon.Warning);
        OpenSettings();
        return _settings.HasValidApiKey;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys.UnregisterAll();
        _hotkeys.Dispose();
        _tray.Dispose();
        _ocr.Dispose();
        _layout.Dispose();
        _settings.Save();
        base.OnExit(e);
    }
}
