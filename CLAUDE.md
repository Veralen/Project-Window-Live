# WindowLive

Local (no-cloud) Windows screen translation tool with two modes: one-shot
desktop snip and continuous live game chat translation. Both modes use a local
LLM backend (llama.cpp + huihui Qwen3.5-0.8B abliterated) — no ONNX, no Windows.Media.Ocr.
Product spec and binding decisions: `docs/window-live-design.md` — read it
before coding anything.

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
- No cloud calls anywhere — all OCR and translation is on-device.
- No disk writes for screenshots — encode to base64 in memory, send,
  discard. Nothing touches disk.
- Core has no WPF/WinRT dependencies — keeps geometry and config
  unit-testable.
- Model files live in `models/` (gitignored, populated on first run);
  never commit them.
- CPU fallback is not supported and must not be silently attempted —
  show a clear error and exit if no supported GPU is detected.
- int8 quantization must never be combined with any GPU execution path —
  this caused uncatchable native crashes in the parent project.

## Key decisions (do not relitigate without good reason)

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
- **ctx-size 512.** Each call is stateless; a tiny context saves VRAM.
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
