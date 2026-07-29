<#
  Now Playing overlay - diagnostics

  Run this on the machine where the overlay is NOT working. It prints a report
  and saves a copy to your Desktop so it is easy to send on.

  It never launches iTunes: every iTunes check is gated on iTunes already
  running, same as the overlay itself.
#>

$ErrorActionPreference = 'SilentlyContinue'
$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$t) { $lines.Add($t); Write-Host $t }
function Head([string]$t) { Say ""; Say "=== $t ===" }

Say "Now Playing overlay - diagnostic report"
Say (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')

# ---------------------------------------------------------------- machine
Head "Machine"
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
Say ("Windows      : " + $os.Caption + " (build " + $os.BuildNumber + ")")
Say ("OS arch      : " + $os.OSArchitecture)
Say ("System type  : " + $cs.SystemType)
Say ("PowerShell   : " + $PSVersionTable.PSVersion)
Say ("PS bitness   : " + $(if ([Environment]::Is64BitProcess) { '64-bit' } else { '32-bit' }))
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')
Say ("Elevated     : " + $admin + $(if ($admin) { '   <-- try running WITHOUT admin; COM is blocked across integrity levels' } else { '' }))

# .NET Framework 4.x is what the .exe needs.
$rel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release
Say ("`.NET Framework: " + $(if ($rel) { "release $rel (OK)" } else { 'NOT FOUND - the .exe cannot run' }))

# ------------------------------------------------------------------ files
Head "Overlay files"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'dist\NowPlayingOverlay.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $here 'NowPlayingOverlay.exe' }
if (Test-Path $exe) {
  $f = Get-Item $exe
  Say ("exe          : " + $f.FullName)
  Say ("size         : " + [Math]::Round($f.Length/1KB,1) + " KB")
  # Files out of a downloaded ZIP carry mark-of-the-web, which can stop them running.
  $zone = Get-Item -LiteralPath $exe -Stream Zone.Identifier
  if ($zone) {
    Say "BLOCKED      : YES - Windows marked this file as downloaded from the internet."
    Say "               Fix: right-click the .exe > Properties > tick Unblock > OK"
    Say "               Or run:  Unblock-File -Path '$exe'"
  } else {
    Say "BLOCKED      : no"
  }
} else {
  Say "exe          : NOT FOUND next to this script (expected dist\NowPlayingOverlay.exe)"
}
$proc = Get-Process NowPlayingOverlay
Say ("running      : " + $(if ($proc) { "yes (pid $($proc.Id))" } else { 'no' }))

# ----------------------------------------------------------------- iTunes
Head "iTunes"
$itProc = Get-WmiObject Win32_Process -Filter "Name='iTunes.exe'"
if ($itProc) {
  foreach ($p in $itProc) { Say ("process      : pid $($p.ProcessId)  $($p.ExecutablePath)") }
} else {
  Say "process      : NOT RUNNING"
  Say "               iTunes must be open for the overlay to read it."
}

# ProgID resolution is the decisive check. Note a Store/packaged iTunes has NO
# HKCR registry entry at all yet still works, so registry alone proves nothing.
$t = [Type]::GetTypeFromProgID('iTunes.Application')
Say ("ProgID lookup: " + $(if ($t) { 'resolves OK' } else { 'NULL' }))
foreach ($v in 'Registry64','Registry32') {
  $b = [Microsoft.Win32.RegistryKey]::OpenBaseKey('ClassesRoot', $v)
  $k = $b.OpenSubKey('iTunes.Application\CLSID')
  Say ("  $v : " + $(if ($k) { $k.GetValue($null) } else { 'no HKCR entry (normal for the Store build)' }))
}

if ($itProc) {
  try {
    $it = New-Object -ComObject iTunes.Application -ErrorAction Stop
    Say ("COM connect  : OK (iTunes " + $it.Version + ")")
    Say ("PlayerState  : " + $it.PlayerState + "   (1 = playing)")
    $tr = $it.CurrentTrack
    if ($tr) { Say ("CurrentTrack : '" + $tr.Name + "' by '" + $tr.Artist + "'") }
    else { Say "CurrentTrack : NULL - nothing loaded, press play in iTunes" }
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($it) | Out-Null
  } catch {
    Say ("COM connect  : FAILED - " + $_.Exception.Message)
  }
} else {
  Say "COM connect  : skipped (not launching iTunes on purpose)"
}

# ------------------------------------------------------------ Apple / appx
Head "Apple apps installed"
$appx = Get-AppxPackage -Name '*Apple*'
if ($appx) { foreach ($a in $appx) { Say ("  " + $a.Name + "  " + $a.Version) } }
else { Say "  none from the Microsoft Store (so any iTunes here is the classic desktop install)" }

# ----------------------------------------------------- Windows media session
Head "Windows media sessions (Spotify, Apple Music, browsers)"
try {
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  function Await($op, $ty) { $x = $asTask.MakeGenericMethod($ty).Invoke($null, @($op)); $x.Wait(-1) | Out-Null; $x.Result }
  [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
  $mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
  $ss = @($mgr.GetSessions())
  Say ("count        : " + $ss.Count)
  foreach ($s in $ss) {
    $pr = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    Say ("  '" + $s.SourceAppUserModelId + "' | " + $s.GetPlaybackInfo().PlaybackStatus + " | '" + $pr.Title + "' - '" + $pr.Artist + "'")
  }
} catch {
  Say ("FAILED to read media sessions: " + $_.Exception.Message)
}

# ------------------------------------------------------------- live server
Head "Overlay server response"
foreach ($port in 8787, 8788) {
  try {
    $r = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$port/sources" -TimeoutSec 5
    Say ("port $port : " + $r.Content)
  } catch {
    Say ("port $port : not responding")
  }
}

# ---------------------------------------------------------------- save out
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'overlay-diagnostic.txt'
Set-Content -LiteralPath $out -Value ($lines -join "`r`n") -Encoding UTF8
Write-Host ""
Write-Host "  Saved to: $out" -ForegroundColor Green
Write-Host "  Send that file (or the text above) back." -ForegroundColor Green
Write-Host ""
# No pause here on purpose - Diagnose.bat handles it, so this stays usable
# when piped or run from another script.
