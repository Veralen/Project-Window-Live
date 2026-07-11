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

## Translation execution provider (CPU default, optional DirectML GPU)

Translation runs on ONNX Runtime. The package is
`Microsoft.ML.OnnxRuntime.DirectML` (1.24.x) — it ships the CPU EP too, so the
default path is unchanged and it merely *adds* an opt-in DirectML (DX12 GPU) EP.

- **CPU is the default and is sacred.** `AppConfig.ExecutionProvider = "cpu"`
  (the default) builds `SessionOptions` exactly as before (ORT_ENABLE_ALL,
  IntraOp = ProcessorCount/2, InterOp = 1). opus-mt on CPU — the shipping engine —
  is byte-for-byte unaffected. On a typical CPU opus-mt translates a short block
  in well under a second, so most users never need the GPU.
- **DirectML is opt-in via config.** `ExecutionProvider = "directml"` (+ optional
  `GpuDeviceId`) runs on any DX12 adapter (NVIDIA/AMD/Intel) with zero install.
  Chosen over CUDA specifically because it's driver-only. `Engine` (`"opus"` /
  `"nllb"`) is also config now so the GPU box can switch to NLLB without a rebuild.
- **One construction point.** `OnnxSessionFactory` is the *only* place
  `SessionOptions` are built and encoder/decoder sessions created. DirectML
  requires (per the ORT DirectML EP docs) `EnableMemoryPattern = false` and
  `ExecutionMode = ORT_SEQUENTIAL`; those are applied only on the DML path, then
  `AppendExecutionProvider_DML(deviceId)`.
- **fp32 for GPU, never int8.** int8 dynamic quantization is CPU-targeted; it does
  not benefit DirectML and — on at least some drivers — int8 + DML triggers a
  *native access violation that no managed try/catch can catch* (it kills the
  process). So the factory refuses int8 + DML and uses CPU instead, and NLLB
  prefers its fp32 weights whenever the provider is DirectML
  (`download-model-nllb.ps1 -Variant fp32`, a ~2.4 GB download).
- **Graceful fallback.** If DirectML *initialization* throws (no DX12 device,
  driver/model unsupported), the factory logs the reason and transparently
  rebuilds CPU sessions — the engine always ends up working. Note this only
  catches *managed* exceptions; the int8-guard above exists precisely because the
  int8+DML failure mode is an uncatchable native crash. Engines expose
  `ActiveProvider` (the provider actually used) after init.
- **DirectML.dll must ship beside the exe.** Microsoft.AI.DirectML's own .targets
  only copies the redistributable when `PlatformTarget` is exactly `x64` — never
  true in our SDK-style AnyCPU projects — and doesn't flow through
  ProjectReferences, so every output silently loaded the *inbox*
  `System32\DirectML.dll` (v1.0, 2020 on Win10), which ORT 1.24's DML EP rejects
  at session creation ("feature level is not supported" → clean CPU fallback).
  ScreenTranslator.Translation.csproj now pins Microsoft.AI.DirectML 1.15.4 and
  copies its `bin\x64-win\DirectML.dll` explicitly; verify the DLL is present in
  any packaged build.
- **Measured on the Arc 140V dev iGPU (2026-07) and re-measured on the RTX 3070
  (2026-07-12, with the correct DirectML.dll 1.15.4):** DirectML is *broken for
  these models on both vendors* — opus-mt fp32 on DML produced garbage output at
  ~13 s/input on the 3070 (~30 s on Arc) vs <2 s clean on CPU; NLLB fp32 on DML
  produced garbage at ~30 s/input; NLLB int8 on DML hard-crashed on Arc (now
  guarded → CPU). Since it reproduces identically on NVIDIA and Intel with the
  proper redistributable, the fault is the KV-cache merged-decoder subgraph on
  the DML EP itself, not drivers/hardware. Treat `ExecutionProvider=directml` as
  non-viable for the current exports.
- **NLLB-200 on CPU (int8, 3070 machine, 2026-07-12):** correct output but
  2.8–24 s per block — not interactive. Consequence: opus-mt on CPU remains the
  only shipping-quality path today.
- **CUDA EP replaced DirectML (2026-07-12) and works.** The package is now
  `Microsoft.ML.OnnxRuntime.Gpu.Windows` (still bundles the CPU EP; CPU default
  unchanged). `ExecutionProvider = "cuda"` (legacy "directml"/"dml" map to cuda
  with a log line). Measured on the RTX 3070, NLLB fp32 on CUDA: **correct
  output at 0.9–4.6 s/block** vs 2.8–24 s int8 on CPU — usable today, before
  any IOBinding work (the decode loop still round-trips the KV cache over PCIe
  per token; device-resident cache binding is the known next optimization).
- **CUDA runtime deployment:** the CUDA 12 / cuDNN 9 DLLs are NOT bundled by the
  NuGet package. They must be loadable by name (exe directory or PATH):
  `cudart64_12`, `cublas64_12`, `cublasLt64_12`, `cufft64_11`, `cudnn*64_9`
  (~2 GB; obtainable from the `nvidia-*-cu12` pip wheels). When absent, CUDA
  init throws and the factory falls back to CPU — NVIDIA-less machines just run
  the CPU path. Dev-layout caveat: cuDNN is loaded by name via plain
  LoadLibrary, which searches the exe dir and PATH but NOT
  `runtimes\win-x64\native\`, so non-published runs need that dir on PATH.
