<#
  Now Playing overlay server  (zero-install, Windows PowerShell 5.1)
  ------------------------------------------------------------------
  Reads the Windows "now playing" media session (SMTC) so it works with
  Spotify, Apple Music, iTunes, and even browser players with NO API keys.

  Serves:
    GET /            -> overlay page (add this URL as an OBS Browser Source)
    GET /np          -> JSON  { playing, title, artist, album, app, id, hasArt }
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

# Prefer real music apps over browsers when several things are "playing".
$script:musicHints = @('spotify', 'apple', 'itunes', 'music', 'tidal', 'deezer')

function Test-IsMusicApp([string]$aumid) {
  if ([string]::IsNullOrEmpty($aumid)) { return $false }
  $l = $aumid.ToLower()
  foreach ($h in $script:musicHints) { if ($l.Contains($h)) { return $true } }
  return $false
}

function Get-BestSession {
  $sessions = @($script:mgr.GetSessions())
  if ($sessions.Count -eq 0) { return $null }
  $best = $null; $bestScore = -1
  foreach ($s in $sessions) {
    try { $status = $s.GetPlaybackInfo().PlaybackStatus } catch { $status = $null }
    $isPlaying = ($status -eq [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Playing)
    $isMusic = Test-IsMusicApp $s.SourceAppUserModelId
    $score = 0
    if ($isPlaying) { $score += 2 }
    if ($isMusic)   { $score += 1 }
    if ($score -gt $bestScore) { $bestScore = $score; $best = $s }
  }
  if ($best) { return $best }
  return $script:mgr.GetCurrentSession()
}

function Get-NowPlaying {
  $s = Get-BestSession
  if (-not $s) { return @{ playing = $false; title = ''; artist = ''; album = ''; app = ''; id = 'none'; hasArt = $false } }
  try {
    $props  = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    $status = $s.GetPlaybackInfo().PlaybackStatus
  } catch {
    return @{ playing = $false; title = ''; artist = ''; album = ''; app = ''; id = 'none'; hasArt = $false }
  }
  $playing = ($status -eq [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Playing)
  $title = if ($props.Title)  { $props.Title }  else { '' }
  $artist = if ($props.Artist) { $props.Artist } else { '' }
  $album = if ($props.AlbumTitle) { $props.AlbumTitle } else { '' }
  $hasArt = ($null -ne $props.Thumbnail)
  $id = [Math]::Abs(("$title|$artist|$album").GetHashCode()).ToString()
  return @{
    playing = $playing; title = $title; artist = $artist; album = $album
    app = $s.SourceAppUserModelId; id = $id; hasArt = $hasArt
  }
}

# Album art rarely changes between polls, so cache the decoded bytes per track id.
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
  try {
    $s = Get-BestSession
    if (-not $s) { return $null }
    $props = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    $ref = $props.Thumbnail
    if (-not $ref) { return $null }
    $stream = Await ($ref.OpenReadAsync()) ($script:TypeRandomAccessStream)
    $netStream = $script:AsStreamForRead.Invoke($null, @($stream))
    $ms = New-Object System.IO.MemoryStream
    $netStream.CopyTo($ms)
    $bytes = $ms.ToArray()
    $ms.Dispose(); $netStream.Dispose()
    if ($bytes.Length -eq 0) { return $null }
    $script:artCacheId = $trackId
    $script:artCacheBytes = $bytes
    $script:artCacheType = Get-ImageMimeType $bytes
    return $bytes
  } catch { return $null }
}

# --- Minimal HTTP server over TcpListener (no admin / no URL reservation needed) ---
function Send-Response($stream, [int]$code, [string]$contentType, [byte[]]$body, [hashtable]$extraHeaders) {
  $statusText = @{ 200 = 'OK'; 204 = 'No Content'; 404 = 'Not Found'; 500 = 'Server Error' }[$code]
  if (-not $statusText) { $statusText = 'OK' }
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine("HTTP/1.1 $code $statusText")
  [void]$sb.AppendLine("Content-Type: $contentType")
  [void]$sb.AppendLine("Content-Length: " + ($(if ($body) { $body.Length } else { 0 })))
  [void]$sb.AppendLine("Access-Control-Allow-Origin: *")
  [void]$sb.AppendLine("Cache-Control: no-cache, no-store, must-revalidate")
  [void]$sb.AppendLine("Connection: close")
  if ($extraHeaders) { foreach ($k in $extraHeaders.Keys) { [void]$sb.AppendLine("$k`: $($extraHeaders[$k])") } }
  [void]$sb.AppendLine("")
  $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($sb.ToString())
  $stream.Write($headerBytes, 0, $headerBytes.Length)
  if ($body -and $body.Length -gt 0) { $stream.Write($body, 0, $body.Length) }
  $stream.Flush()
}

$overlayPath = Join-Path $ScriptDir 'overlay.html'

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()

Write-Host ""
Write-Host "  Now Playing overlay is running." -ForegroundColor Green
Write-Host "  OBS Browser Source URL:  http://127.0.0.1:$Port/" -ForegroundColor Cyan
Write-Host "  Preview in a browser:    http://127.0.0.1:$Port/" -ForegroundColor Cyan
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
          $np = Get-NowPlaying
          $json = ($np | ConvertTo-Json -Compress)
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
        '^/$' {
          if (Test-Path $overlayPath) {
            $html = [System.IO.File]::ReadAllBytes($overlayPath)
            Send-Response $ns 200 'text/html; charset=utf-8' $html
          } else {
            Send-Response $ns 404 'text/plain' ([System.Text.Encoding]::UTF8.GetBytes('overlay.html not found'))
          }
          break
        }
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
}
