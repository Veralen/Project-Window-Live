# WindowLive

Windows screen translation tool with two modes: one-shot desktop snip and
continuous live game chat translation. Two translation providers (Settings →
MODEL): **Local** (default; embedded llama.cpp + huihui Qwen3.5-0.8B
abliterated, fully on-device) and **Custom endpoint** (user-supplied
OpenAI-compatible API — explicit opt-in to off-device calls). Two recognizers
(Settings → OCR): **Vision** (default; send the image to the provider's
multimodal model) and **Tesseract** (local OCR → text-only translation). No
ONNX, no Windows.Media.Ocr. Source-language auto-detection is on-device
(SearchPioneer.Lingua). Product spec and binding decisions:
`docs/window-live-design.md` (+ its 2026-07-19 amendment section) — read it
before coding anything. UI is the design-pack "minimal dark 1b" system
(`Design Pack/design_handoff_project_window_1b/README.md`); all colors/fonts
come from `src/WindowLive.App/Ui/Theme.cs`, never raw values.

## Build & test

- .NET 10 SDK, Windows only. Solution: `WindowLive.slnx`.
- Build: `dotnet build`
- Tests: `dotnet test` (xunit, `tests/WindowLive.Core.Tests`)
- Run app: `dotnet run --project src/WindowLive.App`
  (tray icon + hotkeys Ctrl+Shift+T / Ctrl+Shift+G)
- Quick translation test: `curl` against the llama-server directly —
  no CLI harness project needed

## Hard rules

- All Core geometry is physical pixels, virtual-screen coordinates. DIP
  conversion happens only at the WPF rendering boundary in App. Never
  introduce scaling inside Core.
- The **Local provider makes no cloud calls** — all OCR, detection, and
  translation on-device. Off-device traffic exists ONLY when the user has
  explicitly selected the Custom endpoint provider, and goes only to the
  URL they configured. Never add any other network call. (Amended
  2026-07-19 from "no cloud calls anywhere" — user decision.)
- No disk writes for screenshots — encode to base64 in memory, send,
  discard. Nothing touches disk. (Tesseract consumes PNGs in memory too;
  the only new disk artifacts are `tessdata/*.traineddata` downloads.)
- The Custom API key is stored in plain text in config.json (accepted for
  v1, DPAPI is a known stretch) — treat that file as sensitive; never log
  the key.
- Core has no WPF/WinRT dependencies — keeps geometry and config
  unit-testable.
- Model files live in `models/` (gitignored, populated on first run);
  never commit them.
- CPU fallback is not supported and must not be silently attempted —
  show a clear error and exit if no supported GPU is detected.
- int8 quantization must never be combined with any GPU execution path —
  this caused uncatchable native crashes in the parent project.

## Key decisions (do not relitigate without good reason)

- **Provider abstraction protects the local path (2026-07-19).**
  `LlamaClient`/`TranslationPrompt` are behavior-frozen; providers implement
  `Core.Llm.ITranslationProvider`, recognizers `Core.Ocr.ITextRecognizer`,
  and controllers only talk to the `TranslationBackend` facade. The Local
  provider with a null prompt template issues byte-identical requests to the
  pre-abstraction app (locked by `PromptTemplateTests`).
- **Prompt templates are user-editable** (Settings → PROMPT, placeholders
  `{text}/{source}/{target}`); config stores null for "use built-in
  default" so shipped defaults keep propagating. Local default =
  `PromptTemplate.DefaultLocalTemplate` ≡ the tested few-shot prompt.
- **Custom provider translates the whole transcript in ONE streaming
  chat call** (remote models handle multi-line; per-line round-trips are
  local-only, an artifact of the 0.8B model). Vision transcription on the
  custom provider uses its own plain transcription instruction, NOT the
  local model's empirically-quirky `TranscriptionInstruction`.
- **Tesseract + auto source OCRs with `eng`** (chicken-and-egg: Tesseract
  needs a language before OCR, detection runs after). Pick an explicit
  source language for non-Latin scripts. tessdata_fast files download on
  demand into `tessdata/` beside the exe (gitignored).
- **Language detection = SearchPioneer.Lingua** restricted to the
  `LanguageCatalog` set (short-chat-line accuracy), confidence floor 0.5,
  best-effort only — a detection failure must never break translation.
- **No system prompt.** Use `Translate to English: {input}` as the user
  turn only. System prompts tested less reliably at this model size.
- **Few-shot examples on every call, sentence-level (tested 2026-07-12
  against the live model).** See `window-live-design.md` for the exact
  strings — do not change them without testing. Sentence-level profane
  examples measurably outperform the old single-word ones.
- **Text translation uses the raw `/completion` endpoint, not
  `/v1/chat/completions`** (tested 2026-07-12). Qwen3.5 is a thinking model;
  the chat endpoint's chat template opens a reasoning block that the `"\n"`
  stop sequence kills before any content is produced, yielding empty output.
  `/completion` sends the prompt through with no chat template.
- **Image input is a two-step transcribe-then-translate pipeline**
  (tested 2026-07-12): one-shot image-to-translation does not work at 0.8B.
  Step 1 transcribes the image via `/v1/chat/completions` with
  `"chat_template_kwargs": {"enable_thinking": false}`; step 2 translates
  each transcribed line via the normal `/completion` few-shot path.
- **Image crops are upscaled 2x (HighQualityBicubic) before transcription**
  (tested 2026-07-12) — at 1x the model misreads on-screen text, at 2x it
  reads correctly. In-memory only, via the shared
  `WindowLive.App.Capture.ImageUpscaler` helper.
- **ctx-size 4096, --parallel 2** (revised 2026-07-12 from 512). Image
  transcription rides along on every translation and a realistic snip or
  chat region costs ~1000 image tokens — ctx 512 caused live 400
  exceed_context_size_error failures. Calls are still stateless; --parallel 2
  (instead of the default 4 slots) keeps the KV allocation modest. Image
  area is additionally capped at ~1.0 MP in ImageUpscaler so worst-case
  image tokens stay near ~1000.
- **`--reasoning-budget 0` on server launch.** Belt-and-braces thinking
  suppression on top of the per-request `enable_thinking: false` (which only
  applies to `/v1/chat/completions`, not `/completion`).
- **Dynamic max_tokens:** `clamp(inputChars * 0.75, 30, 120)`. Hard
  ceiling prevents the model writing disclaimers instead of translating.
- **stream: true on every call.** Overlay updates token-by-token.
- **Poll cadence 300ms, skip if bitmap hash unchanged.**
- **Debounce:** wait for one stable frame before sending to model.

## llama-server

Ships beside the exe. App launches it as a child process on startup,
kills it on exit. First run pulls the model from Hugging Face (~500 MB);
surface download progress from llama-server stdout — do not silently hang.

Default port: **8420**.

GPU binary selection via DXGI adapter enumeration:
- Nvidia → `llama-server-cuda.exe`
- AMD / Intel (v2, not yet) → `llama-server-vulkan.exe`
- Neither → clear error dialog, exit

## Project layout

```
src/WindowLive.Core         pure logic: geometry, contracts, config, polling
src/WindowLive.App          WPF shell: hotkeys, overlay, capture, LLM client,
                            streaming display, tray icon
tests/WindowLive.Core.Tests xunit tests for geometry + config
scripts/
  download-model.ps1        first-run model pull via llama-server --hf-repo
  start-server.ps1          child process launch helper
models/                     GGUF files (gitignored)
```

## Parent project

Forked from ScreenTranslator (Project Window). Capture pipeline, overlay
window infrastructure, drag-to-select region UI, and hotkey registration
carry over. The ONNX translation engine, Windows.Media.Ocr, text block
grouper, label placer, and language pack installer are all removed.

## Status (2026-07-19)

v2 (providers + OCR pipeline + languages + design-pack UI) is implemented on
top of the v1 baseline below. What's verified vs. pending:

- **Local+vision snip (regression): verified** via `--snip-rect --save-shot`
  over a Spanish Notepad — new design-pack popup rendered (translation-first,
  demoted original, mint selection). Byte-equality of the default local
  prompt is unit-locked (`PromptTemplateTests`).
- **Local+Tesseract snip: verified** same harness — `eng.traineddata`
  auto-downloaded to `tessdata/` beside the exe, OCR→/completion translation
  displayed. Fixed live: TesseractOCR's `Engine` dataPath must be the
  tessdata folder itself, not its parent.
- **Custom provider: verified** against `tools/FakeOpenAI` (port 8431) —
  llama-server/GPU skipped ("Provider is 'custom'…" log line), vision
  transcription + streamed SSE translation shown in the popup. Also
  smoke-testable against a standalone llama-server on port 8421 (it IS an
  OpenAI-compatible multimodal server) — not yet run this session.
- **Game mode: verified** via `--game-rect` (transcribe + per-line translate
  tasks visible in server log). Fixed live: readiness check must run BEFORE
  `FrameGate.Observe`, else the gate's initial Send is consumed during
  server startup and a static region never retriggers.
- **Pending user (live) verification:** popup Copy/Pin/✕/Retry interactions,
  settings window (apply-immediately controls, health-check status line,
  prompt editing, per-row hotkey rebinds), styled tray menu, new app icon,
  second live in-game test of the reworked game panel (carried over from
  v1), Tesseract on real game text, custom endpoint against a real remote
  API. The `--save-shot` composite clipped the popup footer slightly in
  shot1 — confirm the footer (badge · model · Copy/Pin/✕) looks right live
  before chasing it; may be a harness-render artifact only.
- API key is plaintext in config.json (v1-accepted; DPAPI = stretch).
  FakeOpenAI modes for error paths: `--mode 401|stall|malformed`.

### v1 status (2026-07-12, superseded but context still valid)

- **Snip mode: verified end-to-end** on real screen content via
  `--snip-rect L,T,W,H --save-shot out.png` (correct translation chip
  rendered below a live Spanish Notepad selection).
- **Game mode: reworked after the first live in-game test.** That test
  surfaced (a) update starvation — the old FNV-hash gate required two
  bit-identical frames, which a live game never produces (translations
  only appeared when snip mode's frozen overlay covered the region) — and
  (b) an immovable panel. Fixes: `FrameSignature` (noise-tolerant
  luminance-grid comparison, strict for change-vs-last-sent, tolerant for
  stability) + `FrameGate` force-send backstop (~1.8s worst case under
  constant motion) + transcript-level dedup in `GameModeController` (a
  forced refresh skips translation and leaves the panel untouched when
  the transcribed text is unchanged; `_lastTranscript` commits only after
  a fully streamed translation so failures self-heal). The panel is now a
  normal always-draggable/resizable window (WM_NCHITTEST hook: interior
  drag, edge resize; WS_EX_NOACTIVATE keeps game keyboard focus; NOT
  click-through anymore — user decision) whose rect persists to
  `AppConfig.GamePanelRect` on WM_EXITSIZEMOVE and is reused on every
  ShowFor. All of this awaits the user's second live in-game test — ask
  before assuming it's confirmed.
- Gate thresholds (`FrameGate.ChangeLevelTolerance` etc.) are educated
  defaults validated by unit tests, not against a real game background —
  if updates are still too eager/lazy live, tune those consts first.
- Text quality at 0.8B accepted for v1. Known rough edges: hardest
  profanity garbles rather than echoes now; "gehe afk" once mistranslated
  ("afk" should pass through). Improving = few-shot iteration or a bigger
  model tier (Qwen3.5-2B abliterated was discussed, not pursued).
- If image/OCR accuracy disappoints on large regions: llama-server logs
  suggest `--image-min-tokens 1024` (needs ctx headroom beyond 4096 —
  weigh VRAM). Current approach (≤2x upscale, ~1 MP cap) is accurate on
  chat-sized regions.

Local-only artifacts (gitignored, needed to run/package):

- `runtime/llama-cuda/` — llama.cpp b9966 CUDA build + cudart DLLs, server
  exe duplicated as `llama-server-cuda.exe`. Re-download from ggml-org
  llama.cpp releases if missing (`llama-*-bin-win-cuda-12.4-x64.zip` +
  `cudart-llama-bin-win-cuda-12.4-x64.zip`).
- `models/` — main GGUF (HF-cache layout) + mmproj, auto-downloaded on
  first app run.
- `dist/WindowLive/` — runnable package: `dotnet publish` output + llama
  binaries + seeded models (~1.9 GB). README has the packaging steps.

Testing workflow that worked well:

- Hands-off e2e: launch the exe with `--snip-rect` + `--save-shot`, then
  inspect the PNG. Game mode: `--game-rect L,T,W,H` starts polling on an
  explicit rect with no drag-to-select (transient — doesn't touch saved
  config); point it at a positioned Notepad and watch the app log for
  gate sends / "transcript unchanged" dedup lines.
- Running the Debug-build exe directly needs
  `$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"` first (the
  user-local .NET install isn't found by the apphost otherwise — the app
  silently shows an "install .NET" dialog and never logs). The dist
  build is self-contained and unaffected.
- Prompt/model experiments: run llama-server standalone from
  `runtime/llama-cuda/` and curl `/completion` (text) or
  `/v1/chat/completions` (image transcription). Kill it before launching
  the app — both want port 8420.
- App log: `%LOCALAPPDATA%\WindowLive\logs\app-YYYYMMDD.log` — first stop
  for every user-reported bug this session; stack traces land there.
