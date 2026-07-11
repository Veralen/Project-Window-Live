<#
.SYNOPSIS
    Downloads the local ONNX translation model (Chinese -> English) used by
    ScreenTranslator's LocalOnnxTranslator (Work Package 2).

.DESCRIPTION
    Model: Helsinki-NLP opus-mt-zh-en, ONNX export published at Hugging Face
    repo "Xenova/opus-mt-zh-en" (https://huggingface.co/Xenova/opus-mt-zh-en).
    ~78M-parameter Marian MT model. Small download, fast CPU inference.

    Files are placed in %LOCALAPPDATA%\ScreenTranslator\models (the directory
    AppConfig.ResolveModelDirectory() returns for the default config), unless
    overridden with -Destination.

    Idempotent: a file is skipped when it already exists locally AND its size
    matches the remote Content-Length. Partial / mismatched files are re-fetched.

    Windows PowerShell 5.1 compatible (no '&&', uses Invoke-WebRequest
    -UseBasicParsing).

.PARAMETER Variant
    Which ONNX weight variant(s) to download:
      fp32 (default)      - full-precision encoder + decoder (~446 MB). This is the
                            engine's default: on a typical consumer CPU (no AVX-512
                            VNNI) fp32 measured BOTH faster and higher quality than
                            the int8 model.
      quantized           - int8 encoder + decoder (~113 MB). Smaller download / RAM;
                            still sub-second per block. Use when disk/RAM matters more
                            than latency (construct the engine with preferQuantized:true).
      both                - both sets (for quality/latency comparison).

.PARAMETER Destination
    Target directory. Defaults to %LOCALAPPDATA%\ScreenTranslator\models.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\download-model.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\download-model.ps1 -Variant both
#>
[CmdletBinding()]
param(
    [ValidateSet('quantized', 'fp32', 'both')]
    [string]$Variant = 'fp32',
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # much faster Invoke-WebRequest on PS 5.1

$Repo = 'Xenova/opus-mt-zh-en'
$BaseUrl = "https://huggingface.co/$Repo/resolve/main"

if (-not $Destination -or $Destination.Trim() -eq '') {
    $Destination = Join-Path $env:LOCALAPPDATA 'ScreenTranslator\models'
}

# Tokenizer + config files (always needed). The ONNX weights are flattened out
# of the repo's onnx/ subfolder into the destination root.
$CommonFiles = @(
    'config.json',
    'generation_config.json',
    'tokenizer.json',
    'tokenizer_config.json',
    'special_tokens_map.json',
    'vocab.json',
    'source.spm'
)

$Fp32Files = @(
    @{ Remote = 'onnx/encoder_model.onnx';        Local = 'encoder_model.onnx' },
    @{ Remote = 'onnx/decoder_model_merged.onnx'; Local = 'decoder_model_merged.onnx' }
)
$QuantFiles = @(
    @{ Remote = 'onnx/encoder_model_quantized.onnx';        Local = 'encoder_model_quantized.onnx' },
    @{ Remote = 'onnx/decoder_model_merged_quantized.onnx'; Local = 'decoder_model_merged_quantized.onnx' }
)

# Build the download list.
$Downloads = @()
foreach ($f in $CommonFiles) { $Downloads += @{ Remote = $f; Local = $f } }
if ($Variant -eq 'quantized' -or $Variant -eq 'both') { $Downloads += $QuantFiles }
if ($Variant -eq 'fp32'      -or $Variant -eq 'both') { $Downloads += $Fp32Files }

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
Write-Host "ScreenTranslator model downloader" -ForegroundColor Cyan
Write-Host "  Repo:        $Repo"
Write-Host "  Variant:     $Variant"
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
            Write-Host ("  [skip] {0,-38} {1}" -f $item.Local, (Format-Size $localLen)) -ForegroundColor DarkGray
            $totalOnDisk += $localLen
            continue
        }
        Write-Host ("  [re-dl] {0,-37} local {1} != remote {2}" -f $item.Local, (Format-Size $localLen), (Format-Size $remoteLen)) -ForegroundColor Yellow
    }

    Write-Host ("  [get ] {0,-38} {1} ..." -f $item.Local, (Format-Size $remoteLen)) -NoNewline
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
Write-Host "Done. Point LocalOnnxTranslator at the model directory above." -ForegroundColor Green
