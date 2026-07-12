# WindowLive

Translate anything on your screen — instantly, privately, on your own PC.

WindowLive is a Windows tray app for live game-chat translation and one-shot
screen translation. Point it at foreign text anywhere on screen and an English
translation appears next to it. Everything runs locally on your GPU: no cloud,
no accounts, no screenshots ever leave your machine or touch your disk.

## Two modes

**Desktop snip — `Ctrl+Shift+T`**
Press the shortcut, drag a box around any text on screen, and the translation
appears in a dark chip next to your selection. Dismiss with `Esc`, a click
outside the selection, or the chip's ✕ button.

**Game mode — `Ctrl+Shift+G`**
Press once and drag a box around your game's chat area. A translation panel
anchors itself under that region and updates live as new messages arrive —
polling a few times a second, translating only when the chat actually changes.
The panel is click-through and never steals focus from your game. Press the
shortcut again to redefine the region; pause, resume, or stop from the tray
menu.

## Requirements

- 64-bit Windows 10 (build 19041 / version 2004) or Windows 11
- **Nvidia GPU** with current drivers (CUDA 12). AMD and Intel Arc are not
  supported yet — planned for a future release. There is no CPU fallback:
  a CPU is too slow for live chat translation.
- ~1.5 GB of free VRAM while your game is running
- Internet connection **for the first launch only** (~700 MB model download)

## Getting started

1. Unzip the WindowLive folder anywhere you like. No installer, no admin
   rights needed.
2. Double-click `WindowLive.exe`.
   - It lives in the **system tray** (the icons near the clock, bottom-right).
     If you don't see it, click the `^` (hidden icons) arrow — you can drag
     the icon out to keep it visible.
   - On the very first launch it downloads its translation model from
     Hugging Face (~700 MB) with a progress window. Later launches start in
     a couple of seconds, fully offline.
3. Press `Ctrl+Shift+T` and drag a box around some foreign text. That's it.

Only one copy runs at a time — launching the exe again just points you back
to the tray icon.

## Settings

Right-click the tray icon → **Settings…** to rebind both shortcuts (click a
box, press the keys you want, Save — takes effect immediately). Configuration
is stored at `%APPDATA%\WindowLive\config.json`; poll rate, model, and server
port can be tweaked there.

## Good to know

- **Quality:** translation runs on a small (0.8B) local model, traded for
  privacy and speed. Short chat messages, menus, and captions translate well;
  long dense paragraphs and heavy slang come out rougher. Gaming shorthand
  (gg, ez, afk) passes through untouched.
- **Privacy:** 100% offline after the first-run model download. Captures are
  processed in memory and discarded — nothing is written to disk, nothing is
  sent anywhere.
- Works best on clear, rendered text. Handwriting is not supported.

## Something not working?

WindowLive writes a log that makes problems easy to diagnose:

```
%LOCALAPPDATA%\WindowLive\logs
```

Open the newest `app-YYYYMMDD.log`, or attach it when asking for help.

Common cases:
- **"No supported GPU" on launch** — WindowLive needs an Nvidia GPU with
  CUDA 12 drivers; update your driver or check the GPU is enabled.
- **A shortcut doesn't respond** — another app may own that key combo.
  Rebind it via tray → Settings.
- **"Translation failed" in the overlay** — check the newest log for the
  underlying error; the translation server may still be starting up.

## Building from source

Requires the .NET 10 SDK on Windows.

```
dotnet build          # solution: WindowLive.slnx
dotnet test           # xunit suite (geometry, polling, prompt contract)
dotnet run --project src/WindowLive.App
```

The app expects llama.cpp server binaries. For development, place the CUDA
build (plus its cudart/cublas DLLs) in `runtime/llama-cuda/` at the repo root
with the server exe named `llama-server-cuda.exe` — grab both
`llama-<build>-bin-win-cuda-12.4-x64.zip` and
`cudart-llama-bin-win-cuda-12.4-x64.zip` from
[llama.cpp releases](https://github.com/ggml-org/llama.cpp/releases) and
extract them into that folder. Model files download automatically into
`models/` on first run.

To produce a distributable folder:

```
dotnet publish src/WindowLive.App/WindowLive.App.csproj -c Release -r win-x64 --self-contained true -o dist/WindowLive
```

then copy `llama-server-cuda.exe` and the DLLs from `runtime/llama-cuda/`
into `dist/WindowLive/`. Architecture and binding design decisions live in
[docs/window-live-design.md](docs/window-live-design.md).

## Credits

- Inference: [llama.cpp](https://github.com/ggml-org/llama.cpp) (MIT)
- Model: huihui-ai's Qwen3.5-0.8B abliterated, GGUF quantization by
  [mradermacher](https://huggingface.co/mradermacher/Huihui-Qwen3.5-0.8B-abliterated-GGUF)
- Forked from ScreenTranslator (Project Window)
