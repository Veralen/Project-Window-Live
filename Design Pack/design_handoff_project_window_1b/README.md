# Handoff: Project-Window — Minimal Dark UI (Option 1b)

## Overview
Project-Window is a Windows 10/11 snipping-tool-style overlay translator: the user hits a hotkey, drags a selection over on-screen text, the app OCRs it and shows a floating translation popup near the selection. This handoff covers four surfaces in the chosen "minimal utility" direction: **app icon**, **translation result popup**, **settings window**, and **system tray menu**.

## About the Design Files
The file in this bundle (`1b-reference.html`) is a **design reference created in HTML** — a prototype showing intended look, not production code to copy. Recreate these designs in the target codebase's environment (e.g. Tauri/WinUI/Electron/WPF — whatever the app uses; if none exists yet, pick what fits a lightweight Windows tray utility, e.g. Tauri or WinUI 3) using its established patterns.

## Fidelity
**High-fidelity.** Colors, typography, spacing, and copy are final intent — recreate pixel-perfectly using the codebase's UI toolkit.

## Design Tokens
Colors:
- Window/menu background: `#121212` (settings), `#161616` (popup, tray)
- Deepest background / accent-on-fill text: `#0e0e0e`
- Raised row / tile: `#1c1c1c`, highlighted tray row `#1f1f1f`
- Borders: `#2c2c2c` (controls/windows), `#242424` (internal dividers), `#2e2e2e` (icon tile)
- Text primary: `#f2f2f2` · secondary: `#a8a8a8` / `#c9c9c9` · muted/disabled: `#5c5c5c`
- Accent (single): mint `#47d6a2` — used for selected segment fill, connection status, snip rectangle, language badge, "show" links, icon glyph

Typography:
- UI text: Segoe UI / system-ui. Sizes: 11.5–12px body, 15px popup translation, 10.5px meta.
- Monospace (ui-monospace/Consolas): section labels (10px, 600, letter-spacing .08em, uppercase, `#5c5c5c`), hotkey chips (10.5px), model names (11.5px), popup meta (10px).

Shape: flat, sharp. Border radius 0 on all controls/windows (icon tiles: 12px @96px, 6px @32px, 3px @16px). Borders always 1px solid. Popup shadow: `0 12px 32px rgba(0,0,0,.6)`. No gradients, no blur.

## Screens / Views

### 1. App icon
Dark tile (`#1c1c1c`, 1px `#2e2e2e` border) with white `文` glyph centered, plus two mint corner brackets: top-left (border-top+left 2px `#47d6a2`, 14×14) and bottom-right (border-bottom+right). At 96px the glyph is 40px, weight 400. At 32/16px drop the brackets; glyph rendered in mint `#47d6a2`.

### 2. Translation result popup (330px wide)
Appears anchored below/near the snip rectangle. Snip rectangle itself: 1px solid `#47d6a2`, fill `rgba(71,214,162,.04)`.
- **Translation first**: 15px `#f2f2f2`, line-height 1.55, padding 14px 16px 12px.
- **Original (demoted)**: 11px `#5c5c5c`, padding 0 16px 12px.
- **Footer** (border-top 1px `#242424`, padding 9px 16px, flex gap 10px): mono badge `JA→EN` in mint 10px; model name `qwen2.5:7b` mono 10px `#5c5c5c`; spacer; text buttons `Copy` `Pin` (11px `#a8a8a8`) and `✕` (`#5c5c5c`).
- Hover on text buttons: color → `#f2f2f2`. Copy click: label flips to "Copied" for 1.2s.

### 3. Settings window (400px wide)
Title bar: "Settings" 12px 600 + `✕`, padding 12px 16px, border-bottom `#242424`. Body padding 16px, sections stacked with 18px gap. Each section: uppercase mono label, then controls with 7px gap.
- **MODEL**: 2-segment control (`Ollama` | `Custom endpoint`), full-width, 1px `#2c2c2c` frame; selected segment mint fill `#47d6a2` with `#0e0e0e` 600 text; unselected `#a8a8a8` on transparent. Below: model dropdown (mono value `qwen2.5:7b-instruct`, 1px border, padding 7px 10px, ▾ caret). Below: status line `● connected — localhost:11434` 10.5px mint. When "Custom endpoint" selected, dropdown is replaced by a URL text field.
- **LANGUAGES**: two dropdowns (`Auto-detect` → `English`) side by side, mint `→` between, equal flex.
- **OCR**: 3-segment control `Windows OCR` | `Tesseract` | `Vision` (vision = send image to model), same segment styling, 1px `#2c2c2c` internal dividers.
- **API KEY**: masked mono field `sk-••••••••••••3f2a` `#5c5c5c` with right-aligned mint `show` toggle. Only relevant for custom endpoint; disable (50% opacity) when Ollama selected.
- **HOTKEYS**: 3 rows, label left (11.5px `#a8a8a8`) and key chip right (mono 10.5px `#f2f2f2`, 1px border, padding 2px 7px): Snip & translate `Ctrl+Shift+T`, Translate clipboard `Ctrl+Shift+V`, Re-translate last `Ctrl+Shift+R`. Clicking a chip enters record mode (chip shows "press keys…", border mint).

### 4. Tray menu (230px)
`#161616`, 1px `#2c2c2c`, radius 4px, 4px padding. Rows padding 7px 10px, label 11.5px `#c9c9c9` left, mono shortcut 9.5px `#5c5c5c` right. Hover/first row: `#1f1f1f` fill with `#f2f2f2` label. Items: Snip & translate `^⇧T`, Translate clipboard `^⇧V`, History, ─, Pause hotkeys, Settings, ─, Quit. Dividers 1px `#242424` with 3px 6px margin.

## Interactions & Behavior
- Hotkey → full-screen dim overlay + crosshair drag selection (not yet designed; follow standard snipping tool behavior, selection stroke mint).
- On mouse-up: OCR → translate → popup fades in (120ms ease-out, translate-y 4px) anchored below selection; flip above if no room.
- Popup dismisses on Esc, click-outside, or ✕. "Pin" keeps it always-on-top and draggable.
- While translating: popup shows original text immediately with a 3-dot mint loading indicator in the translation slot.
- Errors (Ollama unreachable, bad API key): translation slot shows message 11.5px `#a8a8a8` + `Retry` text button; status line in settings turns `#d6685c`-equivalent (derive a matching red at same lightness/chroma as the mint).
- Settings apply immediately (no Save button).

## State Management
- `provider`: "ollama" | "custom" · `model` (string) · `endpointUrl` · `apiKey`
- `sourceLang` ("auto" default) · `targetLang` ("en" default)
- `ocrEngine`: "windows" | "tesseract" | "vision"
- `hotkeys`: map of action → accelerator; `paused` boolean
- Popup: `originalText`, `translatedText`, `detectedLang`, `status` (loading/done/error), `pinned`
- Model list fetched from `GET {ollama}/api/tags`; connection status polled/checked on settings open.

## Assets
No external assets. Icon is drawn (glyph + CSS brackets); recreate as SVG/ICO for the real app. Font: Segoe UI (system) + Consolas/ui-monospace.

## Files
- `1b-reference.html` — static reference of all four surfaces, open in a browser.
- Full exploration (all directions): `Project-Window Explorations.dc.html` in the design project, option 1b.
