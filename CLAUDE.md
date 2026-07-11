# ScreenTranslator

Local (no-cloud) Windows screen translation tool: hotkey → snip a region →
OCR (Windows.Media.Ocr) → local ONNX translation → translated labels overlaid
near the original text. Product spec: `local-screen-translator-design.md`.
Binding technical decisions: `docs/architecture.md` — read it before coding.

## Build & test

- .NET 10 SDK, Windows only. Solution: `ScreenTranslator.sln`.
- Build: `dotnet build`
- Tests: `dotnet test` (xunit, tests/ScreenTranslator.Core.Tests)
- Translation harness: `dotnet run --project tools/TranslatorCli -- "你好世界"`
- Run app: `dotnet run --project src/ScreenTranslator.App` (tray icon + global hotkey Ctrl+Alt+T)

## Hard rules

- All Core geometry is physical pixels, virtual-screen coordinates. DPI/DIP
  conversion happens only at the WPF rendering boundary in App. (docs/architecture.md §Coordinate spaces)
- No cloud calls anywhere — OCR and translation are on-device by design.
- Core has no WPF/WinRT dependencies; keep it that way (it's what makes
  grouping/placement unit-testable).
- Model files live in `models/` (gitignored); never commit them.
