<#
  Now Playing overlay server  (zero-install, Windows PowerShell 5.1)
  ------------------------------------------------------------------
  Reads what's playing from two sources and picks the best one:

    1. Windows media session (SMTC) - Spotify, Apple Music, browsers, most apps
    2. iTunes COM automation       - classic/Store iTunes, which does NOT
                                     publish to SMTC

  No API keys, no OAuth, no account linking.

  Serves:
    GET /            -> overlay page (add this URL as an OBS Browser Source)
    GET /layouts     -> side-by-side preview of every layout
    GET /np          -> JSON  { playing, title, artist, album, app, source, id, hasArt }
    GET /art?id=...  -> current album art (image bytes)

  Usage:   powershell -ExecutionPolicy Bypass -File server.ps1 [-Port 8787]
#>

param(
  [int]$Port = 8787
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- WinRT async plumbing (needed to await IAsyncOperation<T> in PS 5.1) ---
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
  Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
  })[0]

function Await($op, $type) {
  $t = $asTaskGeneric.MakeGenericMethod($type).Invoke($null, @($op))
  $t.Wait(-1) | Out-Null
  $t.Result
}

[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null

# PS 5.1 hands back WinRT streams as bare __ComObject, so normal overload
# resolution on AsStreamForRead fails. Bind the method by reflection instead.
$script:TypeRandomAccessStream = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType = WindowsRuntime]
$script:TypeInputStream = [Windows.Storage.Streams.IInputStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$script:AsStreamForRead = [System.IO.WindowsRuntimeStreamExtensions].GetMethod('AsStreamForRead', [type[]]@($script:TypeInputStream))

$script:mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])

function New-TrackInfo($title, $artist, $album, $app, $source, $playing, $hasArt) {
  $t = [string]$title; $a = [string]$artist; $al = [string]$album
  $id = 'none'
  if ($t) { $id = [Math]::Abs(("$t|$a|$al").GetHashCode()).ToString() }
  return @{
    playing = [bool]$playing; title = $t; artist = $a; album = $al
    app = [string]$app; source = [string]$source; id = $id; hasArt = [bool]$hasArt
  }
}

# ======================= SOURCE 1: Windows media session =======================

# Prefer real music apps over browsers when several things are "playing".
$script:musicHints = @('spotify', 'apple', 'itunes', 'music', 'tidal', 'deezer')

function Test-IsMusicApp([string]$aumid) {
  if ([string]::IsNullOrEmpty($aumid)) { return $false }
  $l = $aumid.ToLower()
  foreach ($h in $script:musicHints) { if ($l.Contains($h)) { return $true } }
  return $false
}

function Get-BestSmtcSession {
  $sessions = @($script:mgr.GetSessions())
  if ($sessions.Count -eq 0) { return $null }
  $best = $null; $bestScore = -1
  foreach ($s in $sessions) {
    try { $status = $s.GetPlaybackInfo().PlaybackStatus } catch { $status = $null }
    $isPlaying = ($status -eq [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Playing)
    $score = 0
    if ($isPlaying) { $score += 2 }
    if (Test-IsMusicApp $s.SourceAppUserModelId) { $score += 1 }
    if ($score -gt $bestScore) { $bestScore = $score; $best = $s }
  }
  if ($best) { return $best }
  return $script:mgr.GetCurrentSession()
}

function Get-SmtcNowPlaying {
  $s = Get-BestSmtcSession
  if (-not $s) { return $null }
  try {
    $props  = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    $status = $s.GetPlaybackInfo().PlaybackStatus
  } catch { return $null }
  if (-not $props.Title) { return $null }
  $playing = ($status -eq [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Playing)
  return New-TrackInfo $props.Title $props.Artist $props.AlbumTitle $s.SourceAppUserModelId 'smtc' $playing ($null -ne $props.Thumbnail)
}

function Get-SmtcArtBytes {
  try {
    $s = Get-BestSmtcSession
    if (-not $s) { return $null }
    $props = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    if (-not $props.Thumbnail) { return $null }
    $stream = Await ($props.Thumbnail.OpenReadAsync()) ($script:TypeRandomAccessStream)
    $netStream = $script:AsStreamForRead.Invoke($null, @($stream))
    $ms = New-Object System.IO.MemoryStream
    $netStream.CopyTo($ms)
    $bytes = $ms.ToArray()
    $ms.Dispose(); $netStream.Dispose()
    if ($bytes.Length -eq 0) { return $null }
    return $bytes
  } catch { return $null }
}

# ============================ SOURCE 2: iTunes COM ============================
# iTunes does not publish to SMTC, so it needs its own path. Creating the COM
# object LAUNCHES iTunes if it isn't running, so always gate on the process
# first - otherwise merely starting the overlay would pop iTunes open.

$script:itunesApp = $null

function Clear-ITunesApp {
  if ($script:itunesApp) {
    try { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($script:itunesApp) | Out-Null } catch { }
    $script:itunesApp = $null
  }
}

function Get-ITunesApp {
  if (-not (Get-Process -Name iTunes -ErrorAction SilentlyContinue)) {
    Clear-ITunesApp        # iTunes was closed; drop the dead reference
    return $null
  }
  if ($script:itunesApp) {
    try { $null = $script:itunesApp.PlayerState; return $script:itunesApp }
    catch { Clear-ITunesApp }   # stale RPC handle, fall through and rebuild
  }
  try {
    $script:itunesApp = New-Object -ComObject iTunes.Application
    return $script:itunesApp
  } catch { return $null }
}

function Get-ITunesNowPlaying {
  $it = Get-ITunesApp
  if (-not $it) { return $null }
  try {
    $track = $it.CurrentTrack
    if (-not $track) { return $null }
    $state = $it.PlayerState          # 1 = playing; 0 = stopped/paused
    $hasArt = $false
    try { $hasArt = ($track.Artwork.Count -gt 0) } catch { }
    return New-TrackInfo $track.Name $track.Artist $track.Album 'iTunes' 'itunes' ($state -eq 1) $hasArt
  } catch {
    Clear-ITunesApp
    return $null
  }
}

function Get-ITunesArtBytes {
  $it = Get-ITunesApp
  if (-not $it) { return $null }
  $tmp = $null
  try {
    $track = $it.CurrentTrack
    if (-not $track) { return $null }
    $art = $track.Artwork
    if (-not $art -or $art.Count -lt 1) { return $null }
    $item = $art.Item(1)
    # ITArtworkFormat: 1 = JPEG, 2 = PNG, 3 = BMP
    $ext = switch ([int]$item.Format) { 2 { '.png' } 3 { '.bmp' } default { '.jpg' } }
    $tmp = Join-Path $env:TEMP ("np-itunes-art-" + [Guid]::NewGuid().ToString('N') + $ext)
    $item.SaveArtworkToFile($tmp)
    if (-not (Test-Path $tmp)) { return $null }
    return [System.IO.File]::ReadAllBytes($tmp)
  } catch {
    return $null
  } finally {
    if ($tmp -and (Test-Path $tmp)) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
  }
}

# ============================ Source arbitration ==============================
# Something actively playing always beats something idle, so a paused iTunes
# never steals the overlay from a playing Spotify (or the reverse).

function Get-NowPlaying {
  $candidates = @()
  $smtc = Get-SmtcNowPlaying
  if ($smtc) { $candidates += , $smtc }
  $itunes = Get-ITunesNowPlaying
  if ($itunes) { $candidates += , $itunes }

  if ($candidates.Count -eq 0) { return New-TrackInfo '' '' '' '' 'none' $false $false }

  $best = $null; $bestScore = -1
  foreach ($c in $candidates) {
    $score = 0
    if ($c.playing) { $score += 4 }
    if ($c.source -eq 'itunes' -or (Test-IsMusicApp $c.app)) { $score += 1 }
    if ($score -gt $bestScore) { $bestScore = $score; $best = $c }
  }
  return $best
}

# Album art rarely changes between polls, so cache the decoded bytes per track.
$script:artCacheId = $null
$script:artCacheBytes = $null
$script:artCacheType = 'image/jpeg'

function Get-ImageMimeType([byte[]]$bytes) {
  if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8) { return 'image/jpeg' }
  if ($bytes.Length -ge 4 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50) { return 'image/png' }
  if ($bytes.Length -ge 4 -and $bytes[0] -eq 0x42 -and $bytes[1] -eq 0x4D) { return 'image/bmp' }
  return 'application/octet-stream'
}

function Get-ArtBytes([string]$trackId) {
  if ($trackId -and $script:artCacheId -eq $trackId -and $script:artCacheBytes) {
    return $script:artCacheBytes
  }
  $np = Get-NowPlaying
  $bytes = $null
  if ($np.source -eq 'itunes') { $bytes = Get-ITunesArtBytes }
  elseif ($np.source -eq 'smtc') { $bytes = Get-SmtcArtBytes }
  if (-not $bytes -or $bytes.Length -eq 0) { return $null }
  $script:artCacheId = $trackId
  $script:artCacheBytes = $bytes
  $script:artCacheType = Get-ImageMimeType $bytes
  return $bytes
}

# --- Minimal HTTP server over TcpListener (no admin / no URL reservation needed) ---
function Send-Response($stream, [int]$code, [string]$contentType, [byte[]]$body) {
  $statusText = @{ 200 = 'OK'; 204 = 'No Content'; 404 = 'Not Found'; 500 = 'Server Error' }[$code]
  if (-not $statusText) { $statusText = 'OK' }
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine("HTTP/1.1 $code $statusText")
  [void]$sb.AppendLine("Content-Type: $contentType")
  [void]$sb.AppendLine("Content-Length: " + ($(if ($body) { $body.Length } else { 0 })))
  [void]$sb.AppendLine("Access-Control-Allow-Origin: *")
  [void]$sb.AppendLine("Cache-Control: no-cache, no-store, must-revalidate")
  [void]$sb.AppendLine("Connection: close")
  [void]$sb.AppendLine("")
  $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($sb.ToString())
  $stream.Write($headerBytes, 0, $headerBytes.Length)
  if ($body -and $body.Length -gt 0) { $stream.Write($body, 0, $body.Length) }
  $stream.Flush()
}

function Send-File($stream, [string]$file, [string]$contentType) {
  if (Test-Path $file) {
    Send-Response $stream 200 $contentType ([System.IO.File]::ReadAllBytes($file))
  } else {
    Send-Response $stream 404 'text/plain' ([System.Text.Encoding]::UTF8.GetBytes("$([System.IO.Path]::GetFileName($file)) not found"))
  }
}

$overlayPath = Join-Path $ScriptDir 'overlay.html'
$layoutsPath = Join-Path $ScriptDir 'layouts.html'

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()

Write-Host ""
Write-Host "  Now Playing overlay is running." -ForegroundColor Green
Write-Host "  OBS Browser Source URL:  http://127.0.0.1:$Port/" -ForegroundColor Cyan
Write-Host "  Compare layouts:         http://127.0.0.1:$Port/layouts" -ForegroundColor Cyan
Write-Host "  Sources: Windows media session + iTunes" -ForegroundColor DarkGray
Write-Host "  Press Ctrl+C in this window to stop."
Write-Host ""

try {
  while ($true) {
    $client = $listener.AcceptTcpClient()
    try {
      $client.NoDelay = $true
      $ns = $client.GetStream()
      $ns.ReadTimeout = 3000
      $buf = New-Object byte[] 4096
      $read = $ns.Read($buf, 0, $buf.Length)
      $reqText = [System.Text.Encoding]::ASCII.GetString($buf, 0, $read)
      $path = '/'
      if ($reqText -match '^\s*\w+\s+(\S+)\s+HTTP') { $path = $Matches[1] }
      $route = ($path -split '\?')[0]

      switch -Regex ($route) {
        '^/np$' {
          $json = (Get-NowPlaying | ConvertTo-Json -Compress)
          Send-Response $ns 200 'application/json; charset=utf-8' ([System.Text.Encoding]::UTF8.GetBytes($json))
          break
        }
        '^/art$' {
          $reqId = $null
          if ($path -match 'id=([^&]+)') { $reqId = $Matches[1] }
          $art = Get-ArtBytes $reqId
          if ($art) { Send-Response $ns 200 $script:artCacheType $art }
          else { Send-Response $ns 204 'text/plain' ([byte[]]@()) }
          break
        }
        '^/layouts/?$' { Send-File $ns $layoutsPath 'text/html; charset=utf-8'; break }
        '^/$'          { Send-File $ns $overlayPath 'text/html; charset=utf-8'; break }
        default {
          Send-Response $ns 404 'text/plain' ([System.Text.Encoding]::UTF8.GetBytes('Not found'))
        }
      }
    } catch {
      # swallow per-connection errors so the server keeps running
    } finally {
      $client.Close()
    }
  }
} finally {
  $listener.Stop()
  Clear-ITunesApp
}
