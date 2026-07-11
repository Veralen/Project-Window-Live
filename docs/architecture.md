# ScreenTranslator — Stage 1 architecture (binding decisions)

This document is the contract between work packages. If something here conflicts
with your own judgment, follow this doc and flag the concern in your final report
instead of silently diverging. The product design doc is
`local-screen-translator-design.md` at the repo root — read it first.

## Stack (as revised from the design doc)

- **.NET 10** (not 8 — 10 is the installed LTS SDK), C#, WPF.
- App TFM: `net10.0-windows10.0.19041.0` (gives WinRT projections for
  `Windows.Media.Ocr`). Core/Translation/CLI: plain `net10.0`.
- OCR: `Windows.Media.Ocr`. Default language: **Chinese (Simplified)**, pack
  installed on the dev machine.
- Translation: fully local ONNX via `Microsoft.ML.OnnxRuntime`. Default
  direction: **zh → en**.
- Capture: GDI BitBlt of the **full virtual screen at hotkey press** (see
  Capture-first below).

## Solution layout

```
src/ScreenTranslator.Core         pure logic: geometry, contracts, grouping, placement, config
src/ScreenTranslator.Translation  ONNX translation engine (refs Core)
src/ScreenTranslator.App          WPF shell: hotkey, overlay, capture, OCR, rendering (refs both)
tests/ScreenTranslator.Core.Tests xunit tests for grouping + placement
tools/TranslatorCli               console harness for the translation engine
scripts/                          model download script(s)
models/                           model files (gitignored)
```

## Coordinate spaces (the #1 integration risk — read carefully)

- **Everything in Core is physical (device) pixels in virtual-screen
  coordinates.** Primary monitor top-left = (0,0); negative coords possible.
- The process is PerMonitorV2 DPI-aware (`app.manifest`), so Win32 APIs
  (BitBlt, GetCursorPos, monitor rects from EnumDisplayMonitors) all speak
  physical pixels natively. Do NOT introduce DPI scaling inside Core.
- WPF windows speak device-independent units (DIPs). The App converts at the
  rendering boundary only: `dip = physical / (dpi/96)` for the monitor the
  window is on. Font sizes from `PlacedLabel.FontSize` are physical px and get
  the same conversion.
- OCR runs on a cropped bitmap; `Windows.Media.Ocr` returns image-relative
  rects. The App's `IOcrService` implementation must offset them by the crop's
  screen origin before returning, so everything downstream is screen-space.

## Pipeline (composed in App)

```
hotkey → capture full virtual screen (BitBlt, physical px)
       → snip overlay (frozen screenshot shown dimmed; user drags region)
       → crop region → IOcrService.RecognizeAsync → OcrRegionResult
       → ITextBlockGrouper.Group → TextBlockGroup[]
       → ITranslator.TranslateAsync per block (sequential is fine for Stage 1)
       → ILabelPlacer.Place → PlacedLabel[]
       → overlay renders label chips; stays until Esc / click-outside / close button
```

### Capture-first rule
Capture the whole virtual screen BEFORE showing the overlay, freeze that
screenshot as the overlay background, and crop the user's selection from the
frozen capture. This avoids capturing our own dim layer and makes
overlay-to-image coordinate mapping exact.

## Placement rules (Core, `ILabelPlacer`)

Direction by source-block aspect ratio:
- height > width → **beside**: right first, flip left if it exits `monitorBounds`.
- width ≥ height → **below** first, flip above if it exits bounds or collides.

Sizing: initial font size = median source line height clamped to
[`LabelStyle.MinFontSize`, `MaxFontSize`]; wrap text at max width ≈ source block
width (beside placement: available horizontal margin); shrink font stepwise
until the label footprint ≤ ~1.5× source block area or min font reached.
Measure via `ITextMeasurer` only — no WPF in Core.

Collision handling: labels must not overlap other labels or any source block.
Greedy nudge along the placement axis, bounded iterations; final fallback is
overlapping-but-clamped (never off-screen).

`PlacedLabel.LabelBounds` includes `LabelStyle` padding; App renders the chip
exactly at those bounds (semi-opaque dark chip, light text, subtle border,
`LabelStyle.CornerRadius`).

## Grouping rules (Core, `ITextBlockGrouper`)

Merge vertically adjacent lines into a block when ALL of:
- vertical gap < ~0.7 × median line height,
- left edges aligned within ~1.5 × median char width OR horizontal ranges
  overlap ≥ 50%,
- line heights similar (ratio < 1.6).

Join merged text: CJK language tags (`zh*`, `ja*`, `ko*`) concatenate with no
separator; everything else joins with a single space. A block's `Bounds` is the
union of its line bounds. Never send single OCR lines of one paragraph to
translation separately (grammar breaks across lines).

## Error handling conventions

- Translation model missing → App composes `EchoTranslator` and shows labels
  marked `[no model]`; never crash.
- OCR language pack missing → resolve by BCP-47 prefix match against
  `OcrEngine.AvailableRecognizerLanguages`, fall back to user-profile engine,
  surface a tray balloon/message if nothing matches.
- No text found in region → show a small "no text detected" chip at the region.

## Threading

- OCR + translation off the UI thread (`Task.Run` / async all the way).
- ONNX `InferenceSession` is expensive: create once at startup (background
  init), reuse. `TranslateAsync` calls are serialized by the engine itself
  (single-flight lock) — callers don't need to know.
