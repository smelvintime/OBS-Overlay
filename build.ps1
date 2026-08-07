<#
  Builds NowPlayingOverlay.exe

  Uses the C# compiler that ships with the .NET Framework on every Windows
  machine, so no Visual Studio, no .NET SDK and no downloads are required.

  The overlay pages are embedded into the executable as resources, so the
  result is a single self-contained file.

  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
#>

param(
  [string]$OutDir = "$PSScriptRoot\dist",
  # Product version stamped into the file's Windows metadata.
  [string]$ProductVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'

$csc     = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$winmd   = "$env:windir\System32\WinMetadata"
$fw      = "$env:windir\Microsoft.NET\Framework64\v4.0.30319"
$sysRt   = "$env:windir\Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll"

# Everything is discovered, not listed: every .cs in src\ compiles in, every
# .html in the repo root is embedded (the server serves any embedded page at
# /<name> by convention), and every themes\*.json ships as a built-in theme.
# Adding a page, a theme or a C# module is dropping a file - no build edits.
# Deliberately no installer source: self-copying and shortcut-writing are
# exactly the patterns antivirus heuristics distrust, so the app stays a
# single file the user places wherever they want it.
$srcFiles   = @(Get-ChildItem "$PSScriptRoot\src\*.cs"      | Sort-Object Name)
$pageFiles  = @(Get-ChildItem "$PSScriptRoot\*.html"        | Sort-Object Name)
$themeFiles = @(Get-ChildItem "$PSScriptRoot\themes\*.json" -ErrorAction SilentlyContinue | Sort-Object Name)
$shared     = "$PSScriptRoot\shared.js"
$outExe     = Join-Path $OutDir 'NowPlayingOverlay.exe'

# Fail with a useful message rather than a compiler error further down.
$missing = @()
if (-not (Test-Path $csc))     { $missing += "C# compiler: $csc" }
if (-not (Test-Path $sysRt))   { $missing += "System.Runtime facade: $sysRt" }
foreach ($n in 'Windows.Media.winmd','Windows.Foundation.winmd','Windows.Storage.winmd') {
  if (-not (Test-Path (Join-Path $winmd $n))) { $missing += "WinRT metadata: $n" }
}
if ($srcFiles.Count -eq 0) { $missing += "source files: $PSScriptRoot\src\*.cs" }
# The two pages the app cannot come up without; everything else is optional.
foreach ($core in 'overlay.html','app.html') {
  if (-not ($pageFiles | Where-Object { $_.Name -eq $core })) { $missing += "page: $core" }
}
if (-not (Test-Path $shared)) { $missing += "shared.js" }
# Page names become URLs (/<name>) and resource lookups are case-sensitive,
# so hold the convention at the door instead of debugging it at runtime.
foreach ($p in $pageFiles) {
  if ($p.BaseName -cnotmatch '^[a-z0-9-]+$') {
    $missing += "page name must be lowercase letters/digits/dashes: $($p.Name)"
  }
}
if ($missing.Count) {
  Write-Host ""
  Write-Host "  Cannot build - missing:" -ForegroundColor Red
  $missing | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
  Write-Host ""
  exit 1
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# Windows will not let anyone overwrite a running .exe, so a rebuild while the
# overlay is open fails - and it fails as a raw compiler error (CS0016) that
# reads like a broken build rather than "close the app first". Worse, the same
# lock silently defeats the documented way to update: copying a new exe over
# the old one does nothing while the old one is running, so people carry on
# using the build they were trying to replace and report the bug as unfixed.
#
# Closing it here is safe: running this script IS the request for a new build.
# Whatever was running is started again at the end, so an update is one
# double-click with nothing to remember.
$wasRunning = $false
$running = @(Get-Process -Name 'NowPlayingOverlay' -ErrorAction SilentlyContinue |
             Where-Object { $_.Path -eq $outExe })
if ($running.Count) {
  $wasRunning = $true
  Write-Host ""
  Write-Host "  Closing the running overlay so the new build can replace it ..." -ForegroundColor Yellow
  $running | ForEach-Object { try { $_.CloseMainWindow() | Out-Null } catch {} }
  Start-Sleep -Milliseconds 400
  $running | ForEach-Object {
    try { if (-not $_.HasExited) { Stop-Process -Id $_.Id -Force -ErrorAction Stop } } catch {}
  }
  # The file lock outlives the process by a moment; give it up to ~3s.
  for ($i = 0; $i -lt 30; $i++) {
    try { [IO.File]::Open($outExe, 'Open', 'Write').Close(); break } catch { Start-Sleep -Milliseconds 100 }
  }
}

Write-Host ""
Write-Host "  Building NowPlayingOverlay.exe ..." -ForegroundColor Cyan

# A build stamp compiled straight into the exe. Without one, "which version is
# that machine running?" is unanswerable from anywhere except the machine itself,
# which is exactly when you cannot get to it. Generated into TEMP rather than
# src\ so it never shows up as an untracked file or gets committed by accident.
$stampUtc = (Get-Date).ToUniversalTime()
$version  = $stampUtc.ToString('yyyy.MM.dd.HHmm')
$srcStamp = Join-Path ([IO.Path]::GetTempPath()) 'NowPlayingBuildInfo.cs'
@"
// Generated by build.ps1 - do not edit, do not commit.

// Windows file metadata. An executable with no company, no product name and a
// version of 0.0.0.0 is a shape that heuristic antivirus engines score against
// you: real software nearly always carries this, and a blank one is cheap for
// malware to produce. It costs nothing to be honest about what this is, and it
// also means the file's Properties dialog says something useful.
[assembly: System.Reflection.AssemblyTitle("Now Playing Overlay")]
[assembly: System.Reflection.AssemblyDescription("Now-playing music overlay for OBS, with Twitch alerts and goal boxes")]
[assembly: System.Reflection.AssemblyCompany("smelvintime")]
[assembly: System.Reflection.AssemblyProduct("NowPlayingOverlay")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 smelvintime - https://github.com/smelvintime/OBS-Overlay")]
[assembly: System.Reflection.AssemblyVersion("$ProductVersion.0")]
[assembly: System.Reflection.AssemblyFileVersion("$ProductVersion.0")]

namespace NowPlaying {
  static class BuildInfo {
    public const string Version = "$version";
    public const string BuiltUtc = "$($stampUtc.ToString('yyyy-MM-dd HH:mm:ss')) UTC";
    public const string Product = "$ProductVersion";
  }
}
"@ | Set-Content -Path $srcStamp -Encoding utf8

$cscArgs = @(
  '/nologo'
  # winexe = no console window when double-clicked; it lives in the tray instead.
  # CLI use still prints, via AttachConsole to the parent terminal.
  '/target:winexe'
  '/platform:x64'
  '/optimize+'
  "/out:$outExe"
  "/reference:$fw\System.Windows.Forms.dll"
  "/reference:$fw\System.Drawing.dll"
  "/reference:$winmd\Windows.Media.winmd"
  "/reference:$winmd\Windows.Foundation.winmd"
  "/reference:$winmd\Windows.Storage.winmd"
  "/reference:$sysRt"
  "/reference:$fw\Microsoft.CSharp.dll"
  "/reference:$fw\System.Core.dll"
  # ZipFile lives in the FileSystem assembly and leans on the other; both ship
  # with .NET 4.5+, which Windows 8 onward always has. Used by the self-updater.
  "/reference:$fw\System.IO.Compression.dll"
  "/reference:$fw\System.IO.Compression.FileSystem.dll"
  # WMI, for reading an elevated League client's command line - the fallback
  # discovery tier when MainModule is hidden from a non-elevated overlay.
  "/reference:$fw\System.Management.dll"
)
# Embedded so the .exe needs no files beside it. Resource names are the plain
# file names; the server's convention route and theme discovery key off them.
foreach ($p in $pageFiles)  { $cscArgs += "/resource:$($p.FullName),$($p.Name)" }
$cscArgs += "/resource:$shared,shared.js"
# Built-in themes ride along as data (theme-<file> prefix marks them as such);
# user themes live in %APPDATA%.
foreach ($t in $themeFiles) { $cscArgs += "/resource:$($t.FullName),theme-$($t.Name)" }
# System.Web.Extensions (JavaScriptSerializer, used to read Twitch JSON) is
# already in csc.rsp, so referencing it here would be a duplicate-import error.
foreach ($s in $srcFiles)   { $cscArgs += $s.FullName }
$cscArgs += $srcStamp

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

if ($wasRunning) {
  # It was running when this started, so put it back - otherwise updating
  # silently leaves the overlay closed mid-stream.
  Start-Process $outExe | Out-Null
  Write-Host "  Restarted it - the new build is live in the tray." -ForegroundColor Green
} else {
  Write-Host "  Run it, then add http://127.0.0.1:8787/ as an OBS Browser Source."
}
Write-Host ""

