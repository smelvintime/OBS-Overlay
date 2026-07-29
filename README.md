# Now Playing — Stream Overlay

A self-hosted alternative to Songplz. Shows the song you're currently listening to on your stream.

Works with **Spotify, Apple Music, iTunes, Tidal, and browser players** — with **no API keys, no OAuth, no account linking**. Whatever is playing on this PC is what shows up.

Nothing to install.

## Quick start (recommended: the app)

1. Download **`dist/NowPlayingOverlay.exe`** and double-click it. No window appears —
   it runs quietly in the **system tray** (bottom-right, next to the clock).
2. In OBS: **Sources → + → Browser**
3. Set **URL** to:
   ```
   http://127.0.0.1:8787/
   ```
4. Set **Width** `600`, **Height** `200`
5. Leave **"Shutdown source when not visible"** unchecked so it keeps updating.

That's it — play a song and the card appears. It updates automatically as tracks change.

It's a single 40 KB file. Nothing to install, no PowerShell, no runtime download: it
uses the .NET Framework already present on every Windows 10/11 machine, and the overlay
pages are embedded inside the executable.

> **First run:** Windows SmartScreen will likely say *"Windows protected your PC"*,
> because the file isn't code-signed (a certificate costs a few hundred dollars a year).
> Click **More info → Run anyway**. Some antivirus tools are also suspicious of small
> unsigned executables. If you'd rather not deal with that, use the PowerShell scripts
> below instead — they do exactly the same thing.

Use a different port with `NowPlayingOverlay.exe -port 8788`.

## The tray icon

There's no window. Everything lives on the tray icon (a green ♫). **Right-click** it for:

- **Choose which player to follow…** — opens the source control page
- **Compare layouts…** and **Preview overlay…**
- **Copy OBS browser source URL** — straight to the clipboard
- **Start with Windows** — tick to launch automatically at login
- **Exit** — this is how you stop it

**Double-click** the icon to jump to the source control page. Hovering shows the current
track, so you can confirm it's working at a glance.

> Windows often hides new tray icons behind the **^** arrow. If you don't see it, click
> that arrow — and drag the icon onto the visible part of the taskbar to keep it there.

## Running automatically at startup

Easiest: right-click the tray icon and tick **Start with Windows**.

Or from a terminal:

```
NowPlayingOverlay.exe -startup on
NowPlayingOverlay.exe -startup off
```

This adds a per-user entry under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — no administrator rights needed.
It also appears in **Task Manager → Startup apps**, so you can always disable it there.

A non-default port is preserved: if you set startup while running with `-port 8788`,
it will use that port at login too.

Because it starts silently in the tray, you can leave it enabled permanently and forget
about it — OBS will just find the overlay whenever you stream.

## Alternative: run from source

The PowerShell version needs no build step and is easier to audit or tweak:

1. Double-click **`Start-Overlay.bat`**
2. Same OBS steps as above.

Both serve the identical overlay and support all the same source/layout options. The
`.exe` additionally has the tray icon, start-with-Windows, and the **live equaliser** —
the PowerShell version keeps a console window open and falls back to the animated bars.
For running quietly in the background, use the `.exe`.

## Building the .exe yourself

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Compiles with the C# compiler included in the .NET Framework on every Windows machine —
no Visual Studio, no .NET SDK, no downloads. Output goes to `dist\`.

## Layouts

Open **<http://127.0.0.1:8787/layouts>** to see all four side by side against a
mock stream background, with a copy button for each URL.

| Layout | URL | OBS size | What it is |
|---|---|---|---|
| **Card** (default) | `/?layout=card` | 600 × 200 | Album art, title, artist in a frosted card |
| **Big** | `/?layout=big` | 900 × 320 | Large art and type, cover art blurred behind it |
| **Ticker** | `/?layout=ticker` | 1920 × 64 | Full-width bar with continuously scrolling text |
| **Minimal** | `/?layout=minimal` | 600 × 120 | No card at all — accent bar, art and shadowed text |

Ticker sits nicely along the very top or bottom edge; pair it with `valign=top`.
Big suits a dedicated music scene or a "starting soon" screen.

## Customizing

Add options to the OBS Browser Source URL as query parameters:

```
http://127.0.0.1:8787/?layout=big&accent=fa2d48&showAlbum=1
```

| Option | Values | Default | What it does |
|---|---|---|---|
| `layout` | `card`, `big`, `ticker`, `minimal` | `card` | Which overlay style |
| `accent` | hex, no `#` (e.g. `ff0055`) | `1db954` | Bar/glow/equalizer color |
| `theme` | `glass`, `solid` | `glass` | Card chrome (card and big only) |
| `align` | `left`, `right` | `left` | Which side it sits and animates from |
| `valign` | `top`, `bottom` | `bottom` | Vertical position |
| `scale` | e.g. `1.25` | `1` | Overall size multiplier |
| `radius` | px, e.g. `24` | `18` | Corner roundness |
| `speed` | px/sec, e.g. `90` | `60` | Ticker scroll speed |
| `showAlbum` | `0`, `1` | `0` | Show the album name line |
| `showArt` | `0`, `1` | `1` | Show album art |
| `hideWhenPaused` | `0`, `1` | `0` | `1` hides it while paused instead of dimming |

Some combinations worth trying:

- Spotify green card — `?layout=card&accent=1db954`
- Apple Music pink, right side — `?layout=card&accent=fa2d48&align=right`
- Ticker across the top — `?layout=ticker&valign=top&speed=80`
- Big with album name — `?layout=big&showAlbum=1`
- Text only, no art — `?layout=minimal&showArt=0`
- Only visible while actually playing — `?hideWhenPaused=1`

`theme=minimal` from older setups still works and maps to `layout=minimal`.

## Using a different port

If port 8787 is taken, edit `Start-Overlay.bat` and change `-Port 8787`, then use the
matching port in the OBS URL.

## API (for chat bots / other tools)

| Endpoint | Returns |
|---|---|
| `GET /np` | JSON: `{playing, title, artist, album, app, source, id, hasArt}` |
| `GET /art` | Current album art image bytes (`204` if none) |
| `GET /spectrum` | Server-sent event stream of live frequency bands, ~30fps (.exe only) |
| `GET /spectrum.json` | One-shot spectrum snapshot, handy for debugging (.exe only) |
| `GET /sources` | Diagnostic: what each provider sees, which one won, current pin |
| `GET /setsource?mode=prefer\|only\|auto&app=<name>` | Change which player is followed |
| `GET /` | The overlay page itself |
| `GET /layouts` | Side-by-side layout previews |
| `GET /control` | Source picker page |

If the overlay ever shows the wrong thing, `/sources` is the fastest way to see why —
it reports the Windows media session and iTunes separately, plus which one was chosen.

The included Twitch bot uses `/np`.

## Twitch `!song` command

Lets viewers type `!song` in chat and get the current track as a reply.

### Setup

1. **Get a Twitch OAuth token** with the `chat:read` and `chat:edit` scopes.
   Options, roughly best to worst for trust:
   - [Twitch CLI](https://dev.twitch.tv/docs/cli/) — `twitch token -u -s "chat:read chat:edit"` (official)
   - Register an app in the [Twitch Developer Console](https://dev.twitch.tv/console) and run the OAuth flow
   - A third-party generator like `twitchtokengenerator.com` — quickest, but that site
     sees a token that can post as you. Prefer an official route if you can.

2. **Run `Start-TwitchBot.bat` once.** It creates `twitch-config.json` and exits.

3. **Open `twitch-config.json` and fill it in yourself:**
   ```json
   {
     "channel": "your_channel",
     "botUsername": "your_bot_or_own_username",
     "oauthToken": "oauth:abcd1234..."
   }
   ```
   The token must keep the `oauth:` prefix. You can use your own account as the bot,
   or a separate account if you want a distinct bot name in chat.

4. **Run `Start-TwitchBot.bat` again.** It prints `logged in as ...` when connected.

Keep both windows open while streaming: `Start-Overlay.bat` and `Start-TwitchBot.bat`.

### Bot options (`twitch-config.json`)

| Key | Default | What it does |
|---|---|---|
| `command` | `!song` | The chat trigger |
| `cooldownSeconds` | `10` | Minimum gap between replies, to avoid chat spam |
| `responseTemplate` | `Now playing: {title} - {artist}` | Reply while playing |
| `pausedTemplate` | `Paused: {title} - {artist}` | Reply while paused |
| `notPlayingMessage` | `Nothing playing right now.` | Reply when nothing is loaded |
| `npUrl` | `http://127.0.0.1:8787/np` | Change if you moved the overlay port |

Templates support `{title}`, `{artist}`, `{album}`, and `{app}`.

### Security

`twitch-config.json` holds a token that can post to chat as you. It's listed in
`.gitignore` so it won't be committed — keep it that way, and regenerate the token
in Twitch if it ever leaks. Tokens also expire; if the bot starts reporting a login
failure, generate a fresh one.

### Behavior details

- Ignores its own messages, so it can't loop.
- `!songrequest` and similar won't trigger it — only `!song` exactly, or `!song <args>`.
- Reconnects automatically with backoff if the connection drops.
- Twitch silently drops an identical repeated message within 30s, so repeat answers get
  an invisible character appended to stay visible.
- If the overlay server isn't running, it replies with a clear message rather than
  going silent.

## Live equaliser

The bars next to "Now Playing" are a real spectrum analyser, not an animation.
The app captures whatever Windows is playing, runs an FFT over it, and streams the
frequency bands to the overlay about 30 times a second. **Left bars follow bass, right
bars follow treble** — kick drums punch the left, hi-hats and cymbals flicker on the right.

It works with any audio because it reads the sound card output, not a specific app.
Nothing to configure.

More bars looks better on the bigger layouts:

```
http://127.0.0.1:8787/?layout=big&bars=20
http://127.0.0.1:8787/?layout=card&bars=12
```

| Option | Values | Default | What it does |
|---|---|---|---|
| `bars` | `3`–`32` | `5` | How many equaliser bars |
| `eq` | `live`, `anim`, `off` | `live` | `anim` is the old fixed animation, `off` hides it |

If audio capture is unavailable it falls back to the old animation on its own, so the
overlay never shows a row of dead bars.

**It reflects everything your PC plays**, not just music — Discord, notifications and
game audio move the bars too. If you route music to a separate output device, the
analyser follows the *default* device.

Costs about 0.15% CPU.

## Choosing which player to follow

If more than one thing can make sound — a browser tab, a second music app, a game —
you can stop the overlay switching around. Open:

**<http://127.0.0.1:8787/control>**

It lists every player Windows currently reports, shows what each is playing, and lets
you pick one. Changes apply instantly and are saved, so they survive a restart.

Two ways to pin:

| Setting | Behaviour |
|---|---|
| **Prefer** (default when you pick a source) | That player wins whenever it has something. If it goes silent, the overlay falls back to whatever else is playing. |
| **Only** (tick "Only ever show this source") | Nothing else can ever appear. If that player is silent, the overlay shows nothing. |

Pick **Automatic** to go back to following whatever is actually playing.

You can also pin an app that isn't running yet by typing its name — handy for setting
things up before you open Spotify.

### From the command line

```
NowPlayingOverlay.exe -prefer spotify
NowPlayingOverlay.exe -only itunes
NowPlayingOverlay.exe -auto
```

The name is matched loosely against the app id, so `spotify` matches `Spotify.exe` and
`itunes` matches iTunes. Case doesn't matter. A command-line flag overrides the saved
setting for that run.

Settings live in `%APPDATA%\NowPlayingOverlay\settings.txt`.

## How it finds your music

There are two sources, because no single one covers everything:

1. **Windows media session (SMTC)** — Spotify, Apple Music, Tidal, browsers, most apps.
2. **iTunes COM automation** — iTunes does *not* publish to the Windows media session,
   so it gets its own path. Works with both classic and Microsoft Store iTunes.

Whichever source is *actively playing* wins, so a paused iTunes never steals the overlay
from a playing Spotify (or the reverse). Between two idle sources it prefers a real music
app over a browser tab, so a paused YouTube tab won't hijack it.

**iTunes is only queried while iTunes is already running.** This is deliberate: creating the
iTunes automation object *launches* iTunes, and having the overlay pop iTunes open mid-stream
would be awful. Close iTunes and the overlay simply ignores it.

## Notes and limits

- **Local only.** The server binds to `127.0.0.1`, so it is not reachable from the internet
  and nothing is sent anywhere. Your listening data stays on this machine.
- **Reads this PC**, so it reflects whatever is playing *on this computer*. Music from your
  phone won't appear.
- If the card doesn't appear, confirm the launcher window is still open, and check
  `http://127.0.0.1:8787/np` in a browser to see what the server is reading.
