<#
  Twitch !song bot  (zero-install, Windows PowerShell 5.1)
  --------------------------------------------------------
  Connects to Twitch chat over TLS and answers !song with whatever the
  overlay server (server.ps1) reports as currently playing.

  Requires twitch-config.json next to this script. On first run it copies
  twitch-config.example.json for you to fill in.

  Usage:  powershell -ExecutionPolicy Bypass -File twitch-bot.ps1
#>

param(
  [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ConfigPath) { $ConfigPath = Join-Path $ScriptDir 'twitch-config.json' }

# --- Config -------------------------------------------------------------
if (-not (Test-Path $ConfigPath)) {
  $examplePath = Join-Path $ScriptDir 'twitch-config.example.json'
  if (Test-Path $examplePath) {
    Copy-Item $examplePath $ConfigPath
    Write-Host ""
    Write-Host "  Created twitch-config.json" -ForegroundColor Yellow
    Write-Host "  Open it and fill in your channel, bot username, and OAuth token, then run this again."
    Write-Host "  File: $ConfigPath"
    Write-Host ""
    exit 1
  }
  throw "Config not found: $ConfigPath"
}

$cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json

function Get-CfgValue($obj, [string]$name, $default) {
  $p = $obj.PSObject.Properties[$name]
  if ($p -and $p.Value -ne $null -and "$($p.Value)" -ne '') { return $p.Value }
  return $default
}

$channel     = ([string](Get-CfgValue $cfg 'channel' '')).Trim().TrimStart('#').ToLower()
$botUser     = ([string](Get-CfgValue $cfg 'botUsername' '')).Trim().ToLower()
$token       = ([string](Get-CfgValue $cfg 'oauthToken' '')).Trim()
$command     = ([string](Get-CfgValue $cfg 'command' '!song')).Trim().ToLower()
$cooldownSec = [int](Get-CfgValue $cfg 'cooldownSeconds' 10)
$npUrl       = [string](Get-CfgValue $cfg 'npUrl' 'http://127.0.0.1:8787/np')
$tplPlaying  = [string](Get-CfgValue $cfg 'responseTemplate' 'Now playing: {title} - {artist}')
$tplPaused   = [string](Get-CfgValue $cfg 'pausedTemplate' 'Paused: {title} - {artist}')
$msgNothing  = [string](Get-CfgValue $cfg 'notPlayingMessage' 'Nothing playing right now.')

# Validate before opening a socket, so misconfiguration fails loudly and early.
$problems = @()
if (-not $channel -or $channel -like 'your_twitch_channel*') { $problems += 'channel is not set' }
if (-not $botUser -or $botUser -like 'your_bot_or_own_username*') { $problems += 'botUsername is not set' }
if (-not $token -or $token -like '*PASTE_YOUR_TOKEN_HERE*') { $problems += 'oauthToken is not set' }
if ($token -and $token -notlike 'oauth:*') { $problems += "oauthToken must start with 'oauth:'" }
if ($problems.Count -gt 0) {
  Write-Host ""
  Write-Host "  Config needs attention ($ConfigPath):" -ForegroundColor Yellow
  foreach ($p in $problems) { Write-Host "    - $p" -ForegroundColor Yellow }
  Write-Host ""
  Write-Host "  Get a token with chat:read and chat:edit scopes, then paste it as oauth:xxxxx"
  Write-Host ""
  exit 1
}

# --- Now-playing lookup -------------------------------------------------
function Format-Template([string]$tpl, $np) {
  # Plain string replacement, not -replace: song titles contain $ and \ often
  # enough that regex substitution would mangle them.
  $out = $tpl
  $out = $out.Replace('{title}',  [string]$np.title)
  $out = $out.Replace('{artist}', [string]$np.artist)
  $out = $out.Replace('{album}',  [string]$np.album)
  $out = $out.Replace('{app}',    [string]$np.app)
  return $out
}

function Get-SongMessage {
  try {
    $np = Invoke-RestMethod -Uri $npUrl -TimeoutSec 4
  } catch {
    return "Can't read the music right now (is the overlay running?)"
  }
  if (-not $np -or -not $np.title) { return $msgNothing }
  if ($np.playing) { return (Format-Template $tplPlaying $np) }
  return (Format-Template $tplPaused $np)
}

# --- IRC line parsing (kept pure so it can be tested offline) ------------
function Parse-IrcLine([string]$line) {
  if ($line -match '^PING\s+:?(?<payload>.*)$') {
    return [pscustomobject]@{ Type = 'PING'; Payload = $Matches['payload'] }
  }
  if ($line -match '^:(?<nick>[^!]+)![^ ]+\s+PRIVMSG\s+#(?<chan>[^ ]+)\s+:(?<msg>.*)$') {
    return [pscustomobject]@{
      Type = 'PRIVMSG'; Nick = $Matches['nick'].ToLower()
      Channel = $Matches['chan'].ToLower(); Message = $Matches['msg']
    }
  }
  if ($line -match '\s001\s') { return [pscustomobject]@{ Type = 'WELCOME' } }
  if ($line -match 'NOTICE.*:(Login authentication failed|Improperly formatted auth|Invalid NICK)') {
    return [pscustomobject]@{ Type = 'AUTHFAIL'; Text = $Matches[1] }
  }
  if ($line -match '^RECONNECT') { return [pscustomobject]@{ Type = 'RECONNECT' } }
  return [pscustomobject]@{ Type = 'OTHER'; Text = $line }
}

function Test-IsCommand([string]$message, [string]$cmd) {
  if (-not $message) { return $false }
  $m = $message.Trim().ToLower()
  # Strip Twitch's IRCv3 trailing space and any invisible chars users paste.
  $m = $m.TrimEnd([char]0x20, [char]0x0D, [char]0x0A, [char]0xFFFD)
  return ($m -eq $cmd -or $m.StartsWith($cmd + ' '))
}

# If we send the exact same text twice within 30s Twitch silently drops it,
# so alternate an invisible tag character to keep repeat answers visible.
$script:lastSent = ''
$script:altToggle = $false
function Get-DedupedMessage([string]$text) {
  if ($text -eq $script:lastSent) {
    $script:altToggle = -not $script:altToggle
    if ($script:altToggle) { return $text + ' ' + [char]::ConvertFromUtf32(0xE0000) }
  }
  return $text
}

# --- Connection loop ----------------------------------------------------
$backoff = 5
$script:lastReply = [datetime]::MinValue

Write-Host ""
Write-Host "  Twitch !song bot" -ForegroundColor Green
Write-Host "  channel : #$channel"
Write-Host "  bot     : $botUser"
Write-Host "  command : $command  (cooldown ${cooldownSec}s)"
Write-Host "  source  : $npUrl"
Write-Host "  Press Ctrl+C to stop."
Write-Host ""

while ($true) {
  $tcp = $null; $ssl = $null; $reader = $null; $writer = $null
  try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect('irc.chat.twitch.tv', 6697)
    $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream(), $false)
    $ssl.AuthenticateAsClient('irc.chat.twitch.tv')
    $ssl.ReadTimeout = 6000

    $enc = New-Object System.Text.UTF8Encoding($false)
    $reader = New-Object System.IO.StreamReader($ssl, $enc)
    $writer = New-Object System.IO.StreamWriter($ssl, $enc)
    $writer.AutoFlush = $true

    $writer.WriteLine("PASS $token")
    $writer.WriteLine("NICK $botUser")
    $writer.WriteLine("JOIN #$channel")

    Write-Host "  [$(Get-Date -Format HH:mm:ss)] connected, joining #$channel ..." -ForegroundColor DarkGray
    $backoff = 5

    while ($tcp.Connected) {
      $line = $null
      try { $line = $reader.ReadLine() }
      catch [System.IO.IOException] { continue }   # read timeout: just loop
      if ($null -eq $line) { break }               # server closed connection

      $evt = Parse-IrcLine $line

      switch ($evt.Type) {
        'PING' { $writer.WriteLine("PONG :$($evt.Payload)"); break }
        'WELCOME' { Write-Host "  [$(Get-Date -Format HH:mm:ss)] logged in as $botUser" -ForegroundColor Green; break }
        'AUTHFAIL' {
          Write-Host ""
          Write-Host "  Twitch rejected the login: $($evt.Text)" -ForegroundColor Red
          Write-Host "  Check botUsername and oauthToken in twitch-config.json." -ForegroundColor Red
          Write-Host "  The token needs chat:read and chat:edit scopes, and they expire - regenerate if old." -ForegroundColor Red
          Write-Host ""
          exit 1
        }
        'RECONNECT' { Write-Host "  server asked us to reconnect" -ForegroundColor DarkGray; break }
        'PRIVMSG' {
          if ($evt.Nick -eq $botUser) { break }               # never answer ourselves
          if (-not (Test-IsCommand $evt.Message $command)) { break }

          $since = ([datetime]::Now - $script:lastReply).TotalSeconds
          if ($since -lt $cooldownSec) {
            Write-Host "  [$(Get-Date -Format HH:mm:ss)] $($evt.Nick) used $command (cooling down)" -ForegroundColor DarkGray
            break
          }

          $text = Get-SongMessage
          $send = Get-DedupedMessage $text
          $writer.WriteLine("PRIVMSG #$channel :$send")
          $script:lastSent = $text
          $script:lastReply = [datetime]::Now
          Write-Host "  [$(Get-Date -Format HH:mm:ss)] $($evt.Nick) -> $text" -ForegroundColor Cyan
          break
        }
      }
    }
    throw "disconnected"
  }
  catch {
    if ($_.Exception.Message -ne 'disconnected') {
      Write-Host "  [$(Get-Date -Format HH:mm:ss)] connection problem: $($_.Exception.Message)" -ForegroundColor Yellow
    }
    Write-Host "  reconnecting in ${backoff}s ..." -ForegroundColor DarkGray
    Start-Sleep -Seconds $backoff
    $backoff = [Math]::Min($backoff * 2, 60)
  }
  finally {
    if ($reader) { $reader.Dispose() }
    if ($writer) { $writer.Dispose() }
    if ($ssl)    { $ssl.Dispose() }
    if ($tcp)    { $tcp.Close() }
  }
}
