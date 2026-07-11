# Installs a Windows OCR recognizer language pack. Run in an ELEVATED PowerShell.
# Default: Chinese (Simplified). Usage: right-click > Run as administrator, or:
#   powershell -ExecutionPolicy Bypass -File scripts\install-ocr-language.ps1
param(
    [string]$Capability = "Language.OCR~~~zh-CN~0.0.1.0"
)

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "This script must run elevated. Re-launching with admin rights..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList `
        "-ExecutionPolicy","Bypass","-File","`"$PSCommandPath`"","-Capability","`"$Capability`""
    return
}

Write-Host "Installing OCR capability: $Capability" -ForegroundColor Cyan
Add-WindowsCapability -Online -Name $Capability
Write-Host "Done. Installed OCR capabilities:" -ForegroundColor Green
Get-WindowsCapability -Online -Name "Language.OCR*" |
    Where-Object State -eq "Installed" |
    Select-Object Name, State
Write-Host "`nRestart the app so Windows.Media.Ocr picks up the new recognizer." -ForegroundColor Green
