# Packaging the shippable build

Produces a self-contained, no-install Windows bundle for a non-technical user:
one `ScreenTranslator.exe` (the .NET runtime is bundled) plus a `models/` folder
beside it. Output goes to `dist/ScreenTranslator/` (gitignored).

## Prerequisites
- .NET 10 SDK, Windows x64.
- The translation model already downloaded to
  `%LOCALAPPDATA%\ScreenTranslator\models` (run `scripts/download-model.ps1`).

## Steps

```powershell
# 1. Publish a self-contained single-file x64 exe.
dotnet publish src/ScreenTranslator.App -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist/ScreenTranslator

# 2. Remove build debris (import libs + debug symbols).
Get-ChildItem dist/ScreenTranslator -Include *.lib,*.pdb -Recurse | Remove-Item -Force

# 3. Bundle the model beside the exe. The app's ResolveModelDirectory prefers a
#    "models" folder next to the exe, so this makes the folder drop-in.
$dst = "dist/ScreenTranslator/models"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$env:LOCALAPPDATA\ScreenTranslator\models\*" $dst -Force

# 4. Include the OCR language-pack helper, the NLLB downloader, and the end-user
#    README. README.txt is tracked at packaging/README.txt (edit it there, not in dist/).
Copy-Item scripts/install-ocr-language.ps1 dist/ScreenTranslator -Force
Copy-Item scripts/download-model-nllb.ps1 dist/ScreenTranslator -Force
Copy-Item packaging/README.txt dist/ScreenTranslator -Force

# 5. OPTIONAL — bundle the NLLB multilingual model (config Engine="nllb").
#    The app resolves it from a "models-nllb" folder beside the exe; without it,
#    Engine="nllb" degrades to opus (zh→en). fp32+int8 is ~4.2 GB.
# Copy-Item "$env:LOCALAPPDATA\ScreenTranslator\models-nllb" dist/ScreenTranslator/models-nllb -Recurse -Force

# 6. OPTIONAL — bundle the CUDA runtime for GPU translation (NVIDIA only,
#    config ExecutionProvider="cuda"). Place these DLLs BESIDE the exe (~2 GB):
#    cudart64_12, cublas64_12, cublasLt64_12, cufft64_11, cudnn64_9 and all
#    cudnn_*64_9 DLLs. Source: the nvidia-cuda-runtime-cu12 / nvidia-cublas-cu12 /
#    nvidia-cudnn-cu12 / nvidia-cufft-cu12 pip wheels (win_amd64). The app prepends
#    its exe dir to PATH at startup so these resolve even from the single-file
#    bundle's extraction dir. Without them, "cuda" logs a fallback and runs on CPU.
```

## Model variant vs. size
The bundle can carry either or both model variants (`ResolveModelFile` prefers
fp32, falls back to int8):
- fp32 only (`encoder_model.onnx` + `decoder_model_merged.onnx` + tokenizer): ~434 MB model.
- int8 only (`*_quantized.onnx` + tokenizer): ~117 MB model.
- both: ~541 MB model.
Exe is ~440 MB on top (the bundled ORT CUDA provider is large; it was ~176 MB on
the CPU-only ORT build). On a CPU without AVX-512 VNNI, fp32 is faster *and*
higher quality than int8 (see WP2 notes), so fp32 is the engine default.

## What the end user still needs
The Windows **OCR language pack for the configured source language** is a Windows
capability and cannot be bundled — `install-ocr-language.ps1` installs the
Chinese one (elevated); other languages via `-Capability Language.OCR~~~<tag>` or
Windows Settings > Time & Language. The Settings window warns when the selected
language's pack is missing. This is the only setup step; everything else is in
the folder.

## Distribute
Zip `dist/ScreenTranslator/` and share it. The user unzips, runs the OCR script
once, then runs `ScreenTranslator.exe`.
```powershell
Compress-Archive dist/ScreenTranslator/* dist/ScreenTranslator.zip
```
