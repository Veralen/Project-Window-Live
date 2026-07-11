<#
.SYNOPSIS
    Downloads the NLLB-200-distilled-600M ONNX translation model (int8) used ONLY
    for the offline benchmark path in TranslatorCli (`--engine nllb`). This model is
    NOT wired into the ScreenTranslator app; it exists so a shipping decision can be
    made against the reference model named in the product design doc.

.DESCRIPTION
    Model: facebook/nllb-200-distilled-600M, ONNX export published at Hugging Face
    repo "Xenova/nllb-200-distilled-600M". ~600M-parameter M2M/NLLB seq2seq.

    -Variant selects which encoder + KV-cache-merged decoder weights to fetch:
      int8  (default) — ~small download; CPU-targeted dynamic quantization. This is
                        what the CPU benchmark path uses (current behavior).
      fp32            — full-precision weights (~2.4 GB total). GPU (DirectML) runs
                        want THIS: int8 dynamic quant does not benefit DirectML and
                        can be slower / lower quality. Set ExecutionProvider=directml
                        (config) or --ep directml (CLI) after downloading this.
      both            — download both variants.

    Files are placed in %LOCALAPPDATA%\ScreenTranslator\models-nllb (kept separate
    from the opus-mt models directory), unless overridden with -Destination.

    Idempotent: a file is skipped when it already exists locally AND its size
    matches the remote Content-Length. Partial / mismatched files are re-fetched.

    Windows PowerShell 5.1 compatible (no '&&', uses Invoke-WebRequest -UseBasicParsing).

.PARAMETER Destination
    Target directory. Defaults to %LOCALAPPDATA%\ScreenTranslator\models-nllb.

.PARAMETER Variant
    Which weights to download: int8 (default), fp32 (for GPU/DirectML), or both.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\download-model-nllb.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\download-model-nllb.ps1 -Variant fp32
#>
[CmdletBinding()]
param(
    [string]$Destination,
    [ValidateSet('int8', 'fp32', 'both')]
    [string]$Variant = 'int8'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # much faster Invoke-WebRequest on PS 5.1

$Repo = 'Xenova/nllb-200-distilled-600M'
$BaseUrl = "https://huggingface.co/$Repo/resolve/main"

if (-not $Destination -or $Destination.Trim() -eq '') {
    $Destination = Join-Path $env:LOCALAPPDATA 'ScreenTranslator\models-nllb'
}

# Tokenizer + config files (always needed). The ONNX weights are flattened out
# of the repo's onnx/ subfolder into the destination root.
$CommonFiles = @(
    'config.json',
    'generation_config.json',
    'tokenizer.json',
    'tokenizer_config.json',
    'special_tokens_map.json'
)

# Weight files by variant. int8 (CPU-targeted, default) and/or fp32 (GPU/DirectML).
$Downloads = @()
foreach ($f in $CommonFiles) { $Downloads += @{ Remote = $f; Local = $f } }
if ($Variant -eq 'int8' -or $Variant -eq 'both') {
    $Downloads += @{ Remote = 'onnx/encoder_model_quantized.onnx';        Local = 'encoder_model_quantized.onnx' }
    $Downloads += @{ Remote = 'onnx/decoder_model_merged_quantized.onnx'; Local = 'decoder_model_merged_quantized.onnx' }
}
if ($Variant -eq 'fp32' -or $Variant -eq 'both') {
    $Downloads += @{ Remote = 'onnx/encoder_model.onnx';        Local = 'encoder_model.onnx' }
    $Downloads += @{ Remote = 'onnx/decoder_model_merged.onnx'; Local = 'decoder_model_merged.onnx' }
}

function Get-RemoteLength {
    param([string]$Url)
    try {
        $resp = Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing -MaximumRedirection 5
        return [int64]$resp.Headers['Content-Length']
    } catch {
        return -1
    }
}

function Format-Size {
    param([int64]$Bytes)
    if ($Bytes -lt 0) { return 'unknown' }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

Write-Host ''
Write-Host "ScreenTranslator NLLB benchmark-model downloader" -ForegroundColor Cyan
Write-Host "  Repo:        $Repo"
Write-Host "  Variant:     $Variant$(if ($Variant -ne 'int8') { '  (fp32 is ~2.4 GB; GPU/DirectML wants fp32)' })"
Write-Host "  Destination: $Destination"
Write-Host ''

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$totalDownloaded = [int64]0
$totalOnDisk = [int64]0

foreach ($item in $Downloads) {
    $url = "$BaseUrl/$($item.Remote)"
    $path = Join-Path $Destination $item.Local
    $remoteLen = Get-RemoteLength -Url $url

    if (Test-Path $path) {
        $localLen = (Get-Item $path).Length
        if ($remoteLen -gt 0 -and $localLen -eq $remoteLen) {
            Write-Host ("  [skip] {0,-40} {1}" -f $item.Local, (Format-Size $localLen)) -ForegroundColor DarkGray
            $totalOnDisk += $localLen
            continue
        }
        Write-Host ("  [re-dl] {0,-39} local {1} != remote {2}" -f $item.Local, (Format-Size $localLen), (Format-Size $remoteLen)) -ForegroundColor Yellow
    }

    Write-Host ("  [get ] {0,-40} {1} ..." -f $item.Local, (Format-Size $remoteLen)) -NoNewline
    $tmp = "$path.part"
    try {
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -MaximumRedirection 5
        Move-Item -Force $tmp $path
        $len = (Get-Item $path).Length
        $totalDownloaded += $len
        $totalOnDisk += $len
        Write-Host " done" -ForegroundColor Green
    } catch {
        if (Test-Path $tmp) { Remove-Item -Force $tmp }
        Write-Host " FAILED" -ForegroundColor Red
        throw
    }
}

Write-Host ''
Write-Host ("Downloaded this run: {0}" -f (Format-Size $totalDownloaded))
Write-Host ("Total model size on disk: {0}" -f (Format-Size $totalOnDisk))
Write-Host ("Model directory: {0}" -f $Destination) -ForegroundColor Cyan
Write-Host ''
Write-Host "Done. Benchmark with: dotnet run --project tools/TranslatorCli -- --engine nllb `"<text>`"" -ForegroundColor Green
