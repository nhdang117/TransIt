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

No test project exists yet. The app is a tray-only WPF exe — no console window, no main window on startup.

## Critical: TFM Must Stay Versioned

`TargetFramework` **must** be `net8.0-windows10.0.19041.0` (not `net8.0-windows`). The versioned suffix is what makes `Windows.Media.Ocr`, `Windows.Graphics.Imaging`, and `Windows.Globalization` resolve without any extra NuGet package. Downgrading it will break all WinRT OCR types.

## Architecture

**Tray-only app.** `App.xaml` sets `ShutdownMode="OnExplicitShutdown"`. `App.xaml.cs` owns all wiring: it creates services, registers global hotkeys, and connects tray menu events to the three modes.

### Data Flow (per hotkey)

```
HotkeyManager (WM_HOTKEY via HwndSource)
  → App.xaml.cs dispatcher
    → Mode.ActivateAsync()
      → ScreenCaptureService  → GDI+ Bitmap (physical pixels)
      → OcrService            → List<OcrLine> (logical DIPs)
      → OcrBlock.GroupLines() → List<OcrBlock> (paragraphs)
      → TranslationService    → List<string> (batch, one API call)
      → OverlayTextItem.Build → color/font sampling from bitmap
      → OverlayWindow.ShowOverlay() → Canvas renders on UI thread
```

### Coordinate Systems — Critical Invariant

Two separate spaces exist simultaneously:

| Space | Unit | Used by |
|---|---|---|
| Physical pixels | px | `Bitmap` from GDI+, `RegionSelectWindow` output |
| Logical DIPs | dp | WPF Canvas, OCR `BoundingRect`, `OverlayTextItem.ScreenRect` |

Rule: **only multiply by `dpiScale` when indexing into the `Bitmap` for pixel sampling** (in `ColorSampler` and `OverlayTextItem.Build`). OCR rects drop directly onto the WPF Canvas without scaling. `RegionSelectWindow` multiplies by dpiScale before passing physical rect to `CaptureRegion`.

### Key Files

- **`Infrastructure/NativeMethods.cs`** — all P/Invoke in one place. Add new Win32 declarations here.
- **`Core/OcrService.cs`** — GDI+ Bitmap → `SoftwareBitmap` conversion via `LockBits` + `Marshal.Copy` + `pixels.AsBuffer()`. Uses `using` aliases to avoid name collision: `using AppOcrLine = TransIt.Models.OcrLine` (conflicts with `Windows.Media.Ocr.OcrLine`).
- **`Models/OcrBlock.cs`** — paragraph grouping heuristic: vertical gap < 1.5× avgLineHeight → same block.
- **`Models/OverlayTextItem.cs`** — `Build()` factory samples background/foreground colors from the bitmap and estimates font size from bounding box height.
- **`Windows/Overlay/OverlayWindow.xaml.cs`** — click-through toggle via `WS_EX_TRANSPARENT | WS_EX_LAYERED`. Double-click or Ctrl+A enters annotation mode (InkCanvas). Esc hides.
- **`Modes/RealtimeMode.cs`** — `PeriodicTimer` loop; skips tick if `_isProcessing`; `ChangeDetector` perceptual hash (16×16, Hamming < 5) avoids redundant OCR on static screens; `WinEventHook` forces immediate re-capture on foreground app change.

### Translation Batching

All blocks from one capture are sent as a single JSON array in one API call. OpenAI prompt: `"Return ONLY a valid JSON array of translated strings in the same order..."`. Google uses multiple `&q=` params. `ParseJsonArray` in `TranslationService` strips markdown fences from OpenAI responses.

### Click-Through Overlay

`WS_EX_LAYERED` alone isn't enough on WPF transparent windows — both `WS_EX_TRANSPARENT` and `WS_EX_LAYERED` must be set for true click-through. `SetClickThrough(false)` removes `WS_EX_TRANSPARENT` only (keeps `WS_EX_LAYERED`) to allow InkCanvas input.

### Settings

Persisted to `%APPDATA%\TransIt\settings.json`. `AppSettings.HasValidApiKey` gates all hotkey actions. First run (no key) auto-opens `SettingsWindow`. After settings saved, `App.OpenSettings()` recreates all three modes with the updated `TranslationService`.
