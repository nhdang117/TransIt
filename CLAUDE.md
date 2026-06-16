# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
# Build
dotnet build TransIt/TransIt.csproj

# Run debug
dotnet run --project TransIt/TransIt.csproj

# Build release
dotnet build TransIt/TransIt.csproj -c Release
```

No test project exists. The app is a tray-only WPF exe — no console window, no main window on startup.

## Critical Constraints

**TFM must stay versioned:** `TargetFramework` must be `net8.0-windows10.0.19041.0` (not `net8.0-windows`). The versioned suffix resolves `Windows.Media.Ocr`, `Windows.Graphics.Imaging`, and `Windows.Globalization` without extra NuGet packages.

**DPI awareness:** `app.manifest` declares `PerMonitorV2`. All capture and coordinate code assumes this — never remove or downgrade the manifest.

## Architecture

**Tray-only app.** `App.xaml` sets `ShutdownMode="OnExplicitShutdown"`. `App.xaml.cs` owns all wiring: creates services, registers global hotkeys, connects tray events to three modes. After `SettingsWindow` closes, `App.OpenSettings()` recreates all three mode instances with the new `TranslationService`.

### Hotkeys

| Key | Mode |
|---|---|
| Alt+2 | Snapshot |
| Ctrl+2 | Region |
| Alt+3 | Realtime toggle |

### Data Flow (per hotkey)

```
HotkeyManager (WM_HOTKEY via HwndSource)
  → App.xaml.cs dispatcher
    → Mode.ActivateAsync()
      → ScreenCaptureService.CaptureMonitorAtCursor()  → (Bitmap physpx, dpiScale)
      → OcrService.RecognizeAsync()                    → List<OcrLine> (logical DIPs)
      → OcrBlock.GroupLines()                          → List<OcrBlock> (paragraphs)
      → TranslationService.TranslateAsync()            → List<string> (batch, one API call)
      → OverlayTextItem.Build()                        → color/font sampling from bitmap
      → OverlayWindow.ShowOverlay()                    → Canvas renders on UI thread
```

### Coordinate Systems — Critical Invariant

Two spaces exist simultaneously:

| Space | Unit | Used by |
|---|---|---|
| Physical pixels | px | `Bitmap` from GDI+, `RegionSelectWindow` output, `sampleRect` in `ColorSampler` |
| Logical DIPs | dp | WPF Canvas, OCR `BoundingRect`, `OverlayTextItem.ScreenRect` |

Rules:
- `CaptureMonitorAtCursor()` captures the monitor under the cursor; bitmap origin = monitor's top-left in physical coords.
- OCR rects are in DIP space and drop directly onto WPF Canvas without scaling.
- **Only** multiply by `dpiScale` when indexing into the `Bitmap` for pixel sampling (`ColorSampler`, `OverlayTextItem.Build`).
- `RegionSelectWindow` multiplies logical selection by `dpiScale` before handing `physRect` to `CaptureRegion`.
- In `RegionMode`, bitmap offsets use `physRect.X - monRect.Left` (monitor origin), not `SM_XVIRTUALSCREEN` (virtual screen origin).

### Monitor Detection

`DpiHelper.GetMonitorAtCursor()` and `GetMonitorAtPoint(x, y)` return `(Rectangle physRect, double dpiScale)` by calling `GetCursorPos` → `MonitorFromPoint` → `GetMonitorInfo` + `GetDpiForMonitor`. `ScreenCaptureService.CaptureMonitorAtCursor()` wraps this and returns `(Bitmap, double dpiScale)` as a tuple — callers must deconstruct manually (`using var bitmap = capture.bitmap`), not with `using var`.

### RegionMode Sub-Modes

`RegionMode.ActivateAsync` shows `RegionSelectWindow`, then dispatches based on settings:

| `RegionOverlayMode` | `UseVisionApi` (OpenAI only) | Method |
|---|---|---|
| true | false | `RunOverlayMode` — OCR + translate, overlay on frozen screenshot |
| true | true | `RunVisionOverlayMode` — OCR (rects) + Vision API (text) in parallel |
| false | false | `RunTextPaneMode` — OCR + translate, result in `TextPaneWindow` |
| false | true | `RunVisionMode` — Vision API only, result in `TextPaneWindow` |

### Overlay Window States

`OverlayWindow` has three display states:
- **Live** (`ShowOverlay`): click-through (`WS_EX_TRANSPARENT | WS_EX_LAYERED`), no frozen background, used by Realtime mode.
- **Frozen** (`ShowFrozenOverlay`): interactive (removes `WS_EX_TRANSPARENT`), screenshot as background, installs global Esc keyboard hook.
- **Loading** (`ShowLoadingOverlay`): frozen background with spinner, no text items yet; `UpdateWithTranslation` replaces spinner with items.

Double-click or Ctrl+A on frozen overlay enters annotation mode (InkCanvas). `WS_EX_LAYERED` alone is insufficient for click-through on WPF transparent windows — both flags are required.

### Translation Batching

All blocks from one capture are sent in a single API call. OpenAI: JSON array prompt (`"Return ONLY a valid JSON array..."`); `ParseJsonArray` in `TranslationService` strips markdown fences. Google: multiple `&q=` params in one HTTP request.

### Font Size Estimation

`FontSizeEstimator.Estimate(lineHeightDIP, blockText)` converts OCR tight-box height to WPF `FontSize`:
- Text with descenders (`g j p q y`): multiply by 1.05 (box ≈ 0.95× em)
- Text without descenders: multiply by 1.43 (box ≈ 0.70× em)
- Clamped to [12, 96]

Uses per-line average height, not `block.BoundingRect.Height / lineCount` (union rect includes inter-line gaps).

### Key Files

- **`Infrastructure/NativeMethods.cs`** — all P/Invoke. Add Win32 declarations here only.
- **`Infrastructure/DpiHelper.cs`** — monitor/DPI resolution: `GetMonitorAtCursor()`, `GetMonitorAtPoint()`, `GetDpiScaleForWindow()`.
- **`Core/OcrService.cs`** — GDI+ Bitmap → `SoftwareBitmap` via `LockBits` + `Marshal.Copy` + `pixels.AsBuffer()`. Uses `using AppOcrLine = TransIt.Models.OcrLine` to avoid name collision with `Windows.Media.Ocr.OcrLine`.
- **`Core/ScreenCaptureService.cs`** — `CaptureMonitorAtCursor()` returns `(Bitmap, dpiScale)`; `CaptureRegion(physRect)` captures arbitrary physical rect.
- **`Models/OcrBlock.cs`** — paragraph grouping: vertical gap < 1.5× avgLineHeight → same block.
- **`Models/OverlayTextItem.cs`** — `Build()` factory: samples bg/fg colors from bitmap, calls `FontSizeEstimator`, converts physical rect to logical DIPs.
- **`Services/ChangeDetector.cs`** — perceptual hash (16×16 average hash, Hamming distance < 5 threshold) skips redundant OCR on static screens.
- **`Modes/RealtimeMode.cs`** — `PeriodicTimer` loop; skips tick if `_isProcessing`; `WinEventHook` forces immediate re-capture on foreground app change.

### Settings

Persisted to `%APPDATA%\TransIt\settings.json`. `AppSettings.HasValidApiKey` gates all hotkey actions. First run auto-opens `SettingsWindow`. `UseVisionApi` only applies when `Provider == OpenAI`.

### Debug Log

`OverlayTextItem.Build()` appends sizing diagnostics to `%APPDATA%\TransIt\overlay_debug.log` (dpi, physical height, line count, logical height, font size). Remove once font-size tuning is confirmed.
