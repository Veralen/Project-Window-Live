# Local screen translation tool — design doc

**Target platform:** Windows 10/11 only (this doc assumes Win32/WinRT APIs throughout; no cross-platform abstraction layer needed at this stage).

**Goal:** A snipping-tool-style overlay that OCRs a selected screen region and translates it into a default target language, evolving across three stages toward a live, in-place "AR translation" experience.

---

## Stage 1 — One-shot snip and translate

### User flow
1. User presses global hotkey.
2. Fullscreen semi-transparent overlay appears across all monitors; user drags to select a region.
3. On mouse release: capture the region, run OCR, group text into blocks, translate each block, render translated text near the original (not overlapping it).
4. Overlay stays until dismissed (click outside, Esc, or a small close affordance).

### Recommended stack
| Concern | Choice | Why |
|---|---|---|
| App shell | C# / .NET 8, WPF | Native Windows overlay support, layered/transparent windows, good text rendering control |
| Global hotkey | Win32 `RegisterHotKey` (via P/Invoke) or `NHotkey` NuGet | Simple, reliable system-wide registration |
| Region capture | GDI `BitBlt` for v1 simplicity, or `Windows.Graphics.Capture` (WinRT) if you want DPI-correct capture from the start | `Windows.Graphics.Capture` handles multi-monitor and DPI scaling more cleanly — worth adopting early since Stage 2/3 need it anyway |
| OCR | **`Windows.Media.Ocr`** (built into Windows 10/11) | Free, on-device, no extra dependency, supports many languages out of the box, returns word/line bounding boxes. Use this before reaching for Tesseract/PaddleOCR. |
| Translation | ONNX Runtime + a distilled/quantized NLLB-200 or M2M100 model, invoked via `Microsoft.ML.OnnxRuntime` | Fully local, no cloud call. Quality is below Google/DeepL but acceptable, and this is the only realistic way to keep the "local" constraint for translation on Windows without shelling out to Python (Argos Translate) |
| Overlay rendering | WPF `Window` with `AllowsTransparency=true`, `Topmost=true`, per-text-block `TextBlock` positioned in a `Canvas` | Simple to reason about, good enough for Stage 1 |

### Text block grouping
`Windows.Media.Ocr` gives you words and lines, not paragraphs. Before translating:
1. Merge lines into blocks using proximity + alignment heuristics (similar left-edge X or similar line height and small vertical gaps = same paragraph).
2. Compute a bounding box per merged block.
3. Send each block's full text to translation as one unit — never translate line-by-line, or grammar breaks across line boundaries.

### Placement logic (the specific ask)
For each translated block, decide placement from the **aspect ratio of its bounding box**:

- **Tall/narrow block** (height > width, e.g. a vertical menu column, a sidebar list): place the translation **beside** it — try right side first, flip to left if it would run off-screen.
- **Wide/short block** (width > height, e.g. a paragraph or a horizontal caption): place the translation **above or below** — try below first, flip to above if it would run off-screen or collide with another block.

Implementation notes:
- Compute available screen space in the candidate direction before committing; clamp/flip as needed so nothing renders off-screen.
- Auto-size font so the translated text's rendered footprint stays close to the original's scale — don't let a long translation balloon arbitrarily; cap max width to roughly the original block's width or the available margin, and wrap.
- Run simple collision avoidance across multiple blocks in the same capture (e.g. two adjacent paragraphs) — if two translation labels would overlap, nudge one down/up.
- Give each translation label a subtle background chip (semi-opaque, matching theme) so it's legible over arbitrary content underneath — this avoids needing any inpainting at this stage.

### Stage 1 deliverable
A working hotkey → snip → OCR → translate → labeled overlay loop, single capture only, dismissed manually. This is the whole MVP.

---

## Stage 2 — Persistence (live region, re-processed on change)

### What changes
The overlay pins to the selected region instead of disappearing, and keeps re-translating as the underlying content changes — think a live captions layer over that region.

### Capture strategy
Switch (if not already done in Stage 1) to **DXGI Desktop Duplication API** (`IDXGIOutputDuplication`) instead of GDI `BitBlt`. Key advantage: it natively reports **dirty rectangles** — the specific sub-regions that changed frame to frame — so you get free, hardware-level change detection instead of manually diffing full-frame pixels.

### Reprocessing loop
1. Poll the duplication API at a modest cadence (e.g. every 150–300ms is plenty for UI/subtitle text; no need for 60fps).
2. Intersect reported dirty rects with the pinned region.
3. If the changed area is empty, skip the cycle entirely.
4. If changed, **debounce**: wait for ~200–300ms of no further change before running OCR — text that's mid-scroll or mid-animation shouldn't trigger a translate cycle, since you'd relentlessly re-translate half-rendered frames.
5. Re-run OCR only on the changed sub-area where feasible; keep cached translations for blocks whose bounding box + text hash haven't changed, to avoid re-hitting the translation model unnecessarily.
6. Re-run the Stage 1 placement logic for any block that moved, resized, or changed text.

### New failure modes to design for
- **Flicker**: without debouncing, rapidly changing regions (video, animations) will cause the overlay to flash translated text on/off constantly. Debounce aggressively; consider a "content seems too dynamic to translate" fallback that just hides the overlay for that block until it settles.
- **Drift**: if the window under the pinned region moves or resizes (user drags the app window), the pinned screen coordinates no longer correspond to the same content. Decide explicitly: pin to screen coordinates (simpler, breaks if window moves) or pin to a specific window handle and track its position via `SetWinEventHook` (more robust, more work). Recommend starting with screen-coordinate pinning and documenting the limitation.
- **Cost creep**: continuous OCR + translation, even debounced, is meaningfully more CPU/GPU load than a one-shot snip. Expose a way to pause/resume the live region, and consider downscaling the OCR input resolution for the live loop vs. the higher-fidelity Stage 1 single-shot capture.

### Stage 2 deliverable
A pinned, live-updating region that re-OCRs and re-translates only what changed, with debouncing and drift/flicker mitigations in place.

---

## Stage 3 — Dynamic inpainting (replace text in place)

### What changes
Instead of labeling translations nearby, erase the original on-screen text and draw the translation directly in its place — Google Lens camera-mode style.

### Recommended incremental approach (don't jump straight to generative inpainting)
1. **Flat-fill erase**: for each OCR'd text bounding box, sample a thin border of pixels *just outside* the box (never inside — that risks sampling glyph pixels), take the mode/average color, and fill the box with that flat color before drawing the translated text on top.
   - Works well for flat/solid backgrounds: app UI, menus, subtitles on letterboxed video, plain documents.
   - Looks visibly wrong (smeared/blocky) on photos, gradients, or busy backgrounds — acceptable tradeoff for a first version, and worth explicitly flagging as a known limitation to the user (e.g. a toggle: "in-place mode" vs. "label mode" from Stage 1, so users can fall back when a background is too complex).
2. **Text fitting**: translated text length rarely matches the source. Auto-shrink font size to fit the original box width/height; allow controlled wrapping within the box; as a last resort, allow slight overflow into the erased-and-filled margin rather than clipping.
3. **(Future, optional) True inpainting**: only pursue if flat-fill proves visually inadequate for your actual use cases. Would require a local inpainting model (e.g. a distilled diffusion inpainting model exported to ONNX) run against the erased region — meaningfully heavier compute, real-time performance not guaranteed on CPU-only machines. Treat as a stretch goal, not a Stage 3 requirement.

### Rendering integration with Stage 2's live loop
- On each reprocess cycle, re-erase and re-fill only the bounding boxes that changed (same dirty-rect logic from Stage 2), not the whole pinned region — keeps the live-inpainting loop cheap.
- Layer the erase-and-redraw as an overlay window matching the pinned region's screen coordinates, topmost, so it visually sits atop the real content without modifying anything underneath.

### Stage 3 deliverable
A toggleable in-place mode: flat-fill erase + auto-fit translated text, layered live over the pinned region, with graceful fallback to Stage 1/2's "label nearby" mode for backgrounds where flat-fill looks bad.

---

## Open questions for the implementer
- Source language: auto-detect per block (adds latency, handles mixed-language screens) vs. a single user-set source language (simpler, wrong if content is mixed).
- Multi-monitor + per-monitor DPI scaling: test early: overlay coordinate math needs to account for `Windows.Graphics.Capture`'s DPI-aware coordinates vs. WPF's device-independent units.
- Model size/quality tradeoff for the ONNX translation model — pick based on target hardware (a laptop with no dGPU should probably default to a smaller distilled model, with a settings toggle for a larger one on capable hardware).
- Whether to persist user preferences (target language, label vs. in-place mode, hotkey binding) via a simple local config file.

## Explicit non-goals for all three stages
- No cloud API calls (Google Translate/DeepL/etc.) — everything OCR and translation stays on-device by design.
- No support for non-Windows platforms.
- No handwriting OCR — printed/rendered text only.
