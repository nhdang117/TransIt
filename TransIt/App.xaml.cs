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

    private SnapshotMode?  _snapshotMode;
    private RegionMode?    _regionMode;
    private RealtimeMode?  _realtimeMode;
    private OcrDebugMode?  _ocrDebugMode;

    private CancellationTokenSource? _realtimeCts;
    private bool _realtimeActive;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings   = AppSettings.Load();
        _ocr        = new OcrService();
        _layout     = new LayoutService();
        _translator = new TranslationService(_settings);
        _overlay    = new OverlayWindow();

        _snapshotMode = new SnapshotMode(_ocr, _layout, _translator, _settings, _overlay);
        _regionMode   = new RegionMode(_ocr, _layout, _translator, _settings, _overlay);
        _realtimeMode = new RealtimeMode(_ocr, _layout, _translator, _settings, _overlay);
        _ocrDebugMode = new OcrDebugMode(_ocr, _layout, _settings, _overlay);

        _tray = new TrayIconManager();
        _tray.SnapshotRequested       += (_, _) => RunSnapshot();
        _tray.RegionRequested         += (_, _) => RunRegion();
        _tray.RealtimeToggleRequested += (_, _) => ToggleRealtime();
        _tray.SettingsRequested       += (_, _) => OpenSettings();
        _tray.Initialize();

        _hotkeys = new HotkeyManager();
        _hotkeys.Initialize();
        _hotkeys.HotkeyPressed += OnHotkey;

        bool alt2 = _hotkeys.Register(HotkeyManager.ID_SNAPSHOT,  Infrastructure.NativeMethods.MOD_ALT,     Infrastructure.NativeMethods.VK_2);
        bool ctrl2 = _hotkeys.Register(HotkeyManager.ID_REGION,   Infrastructure.NativeMethods.MOD_CONTROL, Infrastructure.NativeMethods.VK_2);
        bool alt3 = _hotkeys.Register(HotkeyManager.ID_REALTIME,  Infrastructure.NativeMethods.MOD_ALT,     Infrastructure.NativeMethods.VK_3);
        bool ctrl1 = _hotkeys.Register(HotkeyManager.ID_OCR_DEBUG, Infrastructure.NativeMethods.MOD_CONTROL, Infrastructure.NativeMethods.VK_1);

        if (!alt2 || !ctrl2 || !alt3 || !ctrl1)
            _tray.ShowBalloon("TransIt", "Some hotkeys could not be registered (already in use).", BalloonIcon.Warning);

        // First-run: open settings if no API key configured
        if (!_settings.HasValidApiKey)
            OpenSettings();
    }

    private void OnHotkey(object? sender, int id)
    {
        switch (id)
        {
            case HotkeyManager.ID_SNAPSHOT:  RunSnapshot();    break;
            case HotkeyManager.ID_REGION:    RunRegion();      break;
            case HotkeyManager.ID_REALTIME:  ToggleRealtime(); break;
            case HotkeyManager.ID_OCR_DEBUG: RunOcrDebug();    break;
        }
    }

    private void RunSnapshot()
    {
        if (!CheckApiKey()) return;
        _realtimeMode?.Deactivate();
        _realtimeActive = false;
        Task.Run(() => _snapshotMode!.ActivateAsync(CancellationToken.None))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Dispatcher.Invoke(() =>
                        _tray.ShowBalloon("TransIt Error", t.Exception?.InnerException?.Message ?? "Unknown error", BalloonIcon.Error));
            });
    }

    private void RunRegion()
    {
        if (!CheckApiKey()) return;
        _realtimeMode?.Deactivate();
        _realtimeActive = false;
        Task.Run(() => _regionMode!.ActivateAsync(CancellationToken.None))
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
        _realtimeMode?.Deactivate();
        _realtimeActive = false;
        Task.Run(() => _ocrDebugMode!.ActivateAsync(CancellationToken.None))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Dispatcher.Invoke(() =>
                        _tray.ShowBalloon("TransIt Error", t.Exception?.InnerException?.Message ?? "Unknown error", BalloonIcon.Error));
            });
    }

    private void ToggleRealtime()
    {
        if (!CheckApiKey()) return;
        if (_realtimeActive)
        {
            _realtimeCts?.Cancel();
            _realtimeMode?.Deactivate();
            _realtimeActive = false;
            _tray.ShowBalloon("TransIt", "Realtime mode stopped.", BalloonIcon.Info);
        }
        else
        {
            _realtimeActive = true;
            _realtimeCts = new CancellationTokenSource();
            _tray.ShowBalloon("TransIt", "Realtime translation started.", BalloonIcon.Info);
            Task.Run(() => _realtimeMode!.ActivateAsync(_realtimeCts.Token))
                .ContinueWith(t =>
                {
                    _realtimeActive = false;
                    if (t.IsFaulted)
                        Dispatcher.Invoke(() =>
                            _tray.ShowBalloon("TransIt Error", t.Exception?.InnerException?.Message ?? "Unknown error", BalloonIcon.Error));
                });
        }
    }

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings);
        win.ShowDialog();
        // Recreate translator with updated settings
        _translator = new TranslationService(_settings);
        _snapshotMode = new SnapshotMode(_ocr, _layout, _translator, _settings, _overlay);
        _regionMode   = new RegionMode(_ocr, _layout, _translator, _settings, _overlay);
        _realtimeMode = new RealtimeMode(_ocr, _layout, _translator, _settings, _overlay);
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
        _realtimeCts?.Cancel();
        _realtimeMode?.Deactivate();
        _hotkeys.UnregisterAll();
        _hotkeys.Dispose();
        _tray.Dispose();
        _ocr.Dispose();
        _layout.Dispose();
        _settings.Save();
        base.OnExit(e);
    }
}
