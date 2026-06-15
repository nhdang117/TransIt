# TransIt

Real-time screen translation overlay for Windows. Captures screen content via OCR and overlays translated text in-place using WPF.

## Features

- **Snapshot mode** (`Alt+2`) — full-screen capture, freeze, translate, overlay
- **Region mode** (`Ctrl+2`) — select area, translate to overlay or text pane
- **Realtime mode** (`Alt+3`) — continuous OCR + translation with change detection
- Translation providers: **OpenAI** (GPT-4o-mini default) or **Google Translate v2**
- Optional **Vision API** mode (region): uses GPT-4o image understanding instead of OCR
- Click-through overlay; double-click or `Ctrl+A` enters ink annotation mode
- Tray-only app — no main window

## Requirements

- Windows 10 1903+ (build 19041+) — required for WinRT OCR APIs
- .NET 8 SDK
- OpenAI API key **or** Google Cloud Translate API key

## Build & Run

```powershell
# Debug
dotnet run --project TransIt/TransIt.csproj

# Release build
dotnet build TransIt/TransIt.csproj -c Release
```

Output: `TransIt/bin/Release/net8.0-windows10.0.19041.0/TransIt.exe`

## Setup

1. Launch the app — tray icon appears
2. On first run (no API key), Settings window opens automatically
3. Enter your OpenAI or Google Translate API key
4. Set source / target languages
5. Use hotkeys or tray menu

## Settings

Stored at `%APPDATA%\TransIt\settings.json`.

| Field | Default | Description |
|---|---|---|
| `Provider` | `OpenAI` | `OpenAI` or `Google` |
| `OpenAiModel` | `gpt-4o-mini` | Any OpenAI chat model |
| `SourceLanguage` | `en` | BCP-47 language tag |
| `TargetLanguage` | `vi` | BCP-47 language tag |
| `RealtimeIntervalMs` | `2000` | Polling interval (ms) |
| `RegionOverlayMode` | `true` | Region mode shows overlay (vs text pane) |
| `UseVisionApi` | `false` | Use GPT-4o vision instead of OCR |

## Architecture

```
HotkeyManager
  └── Mode.ActivateAsync()
        ├── ScreenCaptureService   GDI+ bitmap (physical px)
        ├── OcrService             WinRT OCR → List<OcrLine> (DIPs)
        ├── OcrBlock.GroupLines()  vertical-gap heuristic → paragraphs
        ├── TranslationService     batch API call → List<string>
        ├── OverlayTextItem.Build  color/font sampling per block
        └── OverlayWindow          WPF Canvas over virtual screen
```

**Coordinate invariant:** OCR rects are in WPF logical DIPs and placed directly on the Canvas. Only `ColorSampler` multiplies by `dpiScale` to index into the physical-pixel bitmap.

## Dependencies

| Package | Purpose |
|---|---|
| `Hardcodet.NotifyIcon.Wpf` 2.0.1 | Tray icon |
| `System.Drawing.Common` 8.0.0 | GDI+ screen capture |
| `System.Text.Json` 9.0.5 | Settings + API serialization |
| Windows SDK (via TFM) | WinRT OCR, SoftwareBitmap |

## Overlay Controls

| Action | Effect |
|---|---|
| `Esc` | Close overlay |
| `Ctrl+A` | Enter ink annotation mode |
| Double-click | Enter ink annotation mode |
| Toolbar → Export | Save PNG of annotated overlay |
| Toolbar → Copy | Copy to clipboard |
