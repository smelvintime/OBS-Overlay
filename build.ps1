<#
  Builds NowPlayingOverlay.exe

  Uses the C# compiler that ships with the .NET Framework on every Windows
  machine, so no Visual Studio, no .NET SDK and no downloads are required.

  The overlay pages are embedded into the executable as resources, so the
  result is a single self-contained file.

  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
#>

param(
  [string]$OutDir = "$PSScriptRoot\dist"
)

$ErrorActionPreference = 'Stop'

$csc     = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$winmd   = "$env:windir\System32\WinMetadata"
$fw      = "$env:windir\Microsoft.NET\Framework64\v4.0.30319"
$sysRt   = "$env:windir\Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll"
$src     = "$PSScriptRoot\src\NowPlayingOverlay.cs"
$overlay = "$PSScriptRoot\overlay.html"
$layouts = "$PSScriptRoot\layouts.html"
$control = "$PSScriptRoot\control.html"
$outExe  = Join-Path $OutDir 'NowPlayingOverlay.exe'

# Fail with a useful message rather than a compiler error further down.
$missing = @()
if (-not (Test-Path $csc))     { $missing += "C# compiler: $csc" }
if (-not (Test-Path $sysRt))   { $missing += "System.Runtime facade: $sysRt" }
foreach ($n in 'Windows.Media.winmd','Windows.Foundation.winmd','Windows.Storage.winmd') {
  if (-not (Test-Path (Join-Path $winmd $n))) { $missing += "WinRT metadata: $n" }
}
foreach ($f in $src,$overlay,$layouts,$control) {
  if (-not (Test-Path $f)) { $missing += "source file: $f" }
}
if ($missing.Count) {
  Write-Host ""
  Write-Host "  Cannot build - missing:" -ForegroundColor Red
  $missing | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
  Write-Host ""
  exit 1
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Write-Host ""
Write-Host "  Building NowPlayingOverlay.exe ..." -ForegroundColor Cyan

$cscArgs = @(
  '/nologo'
  '/target:exe'
  '/platform:x64'
  '/optimize+'
  "/out:$outExe"
  "/reference:$winmd\Windows.Media.winmd"
  "/reference:$winmd\Windows.Foundation.winmd"
  "/reference:$winmd\Windows.Storage.winmd"
  "/reference:$sysRt"
  "/reference:$fw\Microsoft.CSharp.dll"
  "/reference:$fw\System.Core.dll"
  # embedded so the .exe needs no files beside it
  "/resource:$overlay,overlay.html"
  "/resource:$layouts,layouts.html"
  "/resource:$control,control.html"
  $src
)

$output = & $csc @cscArgs 2>&1
$code = $LASTEXITCODE

if ($code -ne 0 -or -not (Test-Path $outExe)) {
  Write-Host ""
  Write-Host "  BUILD FAILED" -ForegroundColor Red
  $output | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
  exit 1
}

# Warnings still matter even on a successful build.
$warnings = $output | Where-Object { "$_" -match 'warning' }
if ($warnings) {
  Write-Host "  warnings:" -ForegroundColor Yellow
  $warnings | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
}

$size = [Math]::Round((Get-Item $outExe).Length / 1KB, 1)
Write-Host ""
Write-Host "  Built: $outExe  (${size} KB)" -ForegroundColor Green
Write-Host "  Run it, then add http://127.0.0.1:8787/ as an OBS Browser Source."
Write-Host ""
