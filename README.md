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
- **Twitch alerts and stats** — previews, source URLs, and the connection state
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
mock stream background. Each one has an **opacity slider** and a **new-song
transition** picker that write straight into the URL you copy, plus a
**Preview** button so you can watch the transition without skipping a track.

| Layout | URL | OBS size | What it is |
|---|---|---|---|
| **Card** (default) | `/?layout=card` | 600 × 200 | Album art, title, artist in a frosted card |
| **Big** | `/?layout=big` | 900 × 320 | Large art and type, cover art blurred behind it |
| **Ticker** | `/?layout=ticker` | 1920 × 64 | Full-width bar with continuously scrolling text |
| **Minimal** | `/?layout=minimal` | 600 × 120 | No card at all — accent bar, art and shadowed text |

Ticker sits nicely along the very top or bottom edge; pair it with `valign=top`.
Big suits a dedicated music scene or a "starting soon" screen.

The same page has a **Twitch sources** section below the layouts, for the follow/sub
alerts and the follower/subscriber boxes — see
[Twitch alerts and follower/sub boxes](#twitch-alerts-and-followersub-boxes).

## Customizing

Open **<http://127.0.0.1:8787/customize>** (or right-click the tray icon →
*Customize the overlay*). Every option below has a control there, with a live
preview of whatever is playing right now, a background switcher so you can
check it stays readable over gameplay, and a **Replay song change** button.
Tabs at the top switch between the three OBS sources — the now-playing overlay,
the Twitch alerts, and the follower/subscriber boxes.
Copy the URL from the bar at the bottom when it looks right. Your choices are
remembered in that browser, and **Reset all** puts everything back.

Colour, opacity, size and corner roundness apply to the preview as you drag,
without reloading it. The rest reload the preview frame, which takes a moment.

Everything is just query parameters, so you can hand-edit the URL too:

```
http://127.0.0.1:8787/?layout=big&accent=fa2d48&showAlbum=1
```

| Option | Values | Default | What it does |
|---|---|---|---|
| `layout` | `card`, `big`, `ticker`, `minimal` | `card` | Which overlay style |
| `accent` | hex, no `#` (e.g. `ff0055`), or `auto` | `1db954` | Bar/glow/equalizer color. `auto` takes it from the album art |
| `theme` | `glass`, `solid` | `glass` | Card chrome (card and big only) |
| `align` | `left`, `right` | `left` | Which side it sits and animates from |
| `valign` | `top`, `bottom` | `bottom` | Vertical position |
| `scale` | e.g. `1.25` | `1` | Overall size multiplier |
| `radius` | px, e.g. `24` | `18` | Corner roundness |
| `speed` | px/sec, e.g. `90` | `60` | Ticker scroll speed |
| `opacity` | `10`–`100` | `100` | Overall overlay opacity, in percent |
| `transition` | `slide`, `drop`, `fade`, `none` | `slide` | How a new song arrives |
| `eq` | `live`, `anim`, `off` | `live` | Equalizer: real audio, a canned loop, or hidden |
| `bars` | `3`–`32` | `5` | How many equalizer bars |
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
- Sits back behind gameplay — `?layout=minimal&opacity=65`
- Colour follows the album art — `?layout=big&accent=auto`

### Accent from the album art

`accent=auto` samples the cover and recolours the accent bar, equalizer, glow
and ticker edge to match, changing with every song. It votes on hue rather than
averaging the cover, because averaging a red half and a blue half gives a muddy
grey that appears nowhere in the art. Pixels are weighted by how much colour
they actually contain, so one vivid patch beats a large dim background — the
colour you would name if asked. The result is nudged into a readable
saturation and lightness range, so a very dark or very pale cover still gives a
legible highlight. A greyscale cover, or a track with no art, falls back to the
default accent.

`theme=minimal` from older setups still works and maps to `layout=minimal`.

### Song changes

When the track changes, the overlay itself stays put and only its contents
swap: the old title and art leave, the new ones arrive from the opposite edge
and settle with a slight overshoot. `slide` comes in from the right, `drop`
from the top, `fade` cross-fades in place, and `none` switches instantly. Only
the very first song plays the full entry animation.

The ticker works differently, because restarting a scrolling line chops it off
mid-word. It runs as a conveyor instead — copies are added at the right edge as
room appears and dropped once they clear the left — so a new song simply starts
filling in behind the line already on screen. Nothing is ever cut off, and
queued copies of the old track that haven't reached the screen yet are dropped
so the new one arrives as soon as it can.

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
| `GET /alerts` | Twitch follow/sub alert source (.exe only for live data) |
| `GET /stats` | Twitch follower and subscriber boxes (.exe only for live data) |
| `GET /twitch` | JSON: connection status, follower/sub totals, goals, most recent of each |
| `GET /twitch/events` | Server-sent event stream of follows and subs (.exe only) |
| `GET /twitch/test?type=follow\|sub\|resub\|gift&user=<name>` | Fire a fake alert, for styling without waiting for a real one |
| `GET /layouts` | Side-by-side layout previews |
| `GET /customize` | Full customizer with a live preview |
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

`twitch-config.json` holds a token that can post to chat as you, and — once the
alerts are set up — a second one that can read your follower and subscriber lists.
It's listed in `.gitignore` so it won't be committed — keep it that way, and
regenerate either token in Twitch if it ever leaks. Tokens also expire; if the bot
reports a login failure, or the tray menu says the token expired, generate a fresh one.

### Behavior details

- Ignores its own messages, so it can't loop.
- `!songrequest` and similar won't trigger it — only `!song` exactly, or `!song <args>`.
- Reconnects automatically with backoff if the connection drops.
- Twitch silently drops an identical repeated message within 30s, so repeat answers get
  an invisible character appended to stay visible.
- If the overlay server isn't running, it replies with a clear message rather than
  going silent.

## Twitch alerts and follower/sub boxes

Two more browser sources, separate from the now-playing card so they can be
positioned independently:

| Source | URL | What it does |
|---|---|---|
| **Alerts** | `/alerts` | Fires when someone follows, subscribes, resubscribes or gifts. Invisible between events, so it can cover the whole canvas. |
| **Followers & subs** | `/stats` | Two boxes stacked on top of each other, each alternating between who was most recent and a progress bar to the next goal. |

Both need `NowPlayingOverlay.exe` — `server.ps1` serves the pages but does not talk
to Twitch, so under it they stay empty.

### Setup

This needs a **second, different token** from the `!song` bot. The chat token has the
wrong scopes and belongs to a different client, so it will not work here.

1. **Register an app** in the [Twitch Developer Console](https://dev.twitch.tv/console).
   Note its **Client ID** — this one is not secret.

2. **Get a user token** for your own account. Only one scope is actually required:

   | Scope | Needed for | Required? |
   |---|---|---|
   | `moderator:read:followers` | Follower alerts, count and latest follower | **Yes** |
   | `channel:read:subscriptions` | Subscriber alerts and count | Optional |

   Easiest official route is the [Twitch CLI](https://dev.twitch.tv/docs/cli/).
   Followers only — the simplest thing that works:
   ```
   twitch token -u -s "moderator:read:followers"
   ```
   Or both, if you want subscriber alerts too:
   ```
   twitch token -u -s "moderator:read:followers channel:read:subscriptions"
   ```

   A followers-only token is a fully supported setup, not a degraded one. The
   subscriber box hides itself, sub alerts simply never fire, and follows work
   exactly as they would otherwise.

3. **Put both in `twitch-config.json`** next to the app (it is read from the exe's
   folder or up to three levels above, so `dist\` works):
   ```json
   {
     "channel": "your_channel",
     "clientId": "your_client_id",
     "apiToken": "your_user_token",
     "followerGoal": 0,
     "subGoal": 0
   }
   ```

4. **Restart the app.** The tray menu's *Twitch alerts and stats* submenu shows the
   connection state, and `/customize` shows a banner explaining any problem.

> **The most common mistake:** using a Client ID that did not issue the token. If you
> used a generator site rather than your own app, you must use *that site's* Client ID.
> Twitch checks the pair and rejects every request otherwise — which shows up as
> `Twitch rejected the token (401)`.

### Testing it with a second account

You can't follow your own channel, so a real end-to-end test needs another account.

1. Open **`/customize`** and pick the **Twitch alerts** tab. Leave it on screen.
2. The banner at the top should read *Connected to Twitch as &lt;you&gt;, listening for
   **follows***, followed by a running count of events received.
3. Follow the channel from the second account.
4. Within a second or two the count ticks up to *1 event received — last was
   **channel.follow** at hh:mm:ss*, and the alert plays in the preview.

That count is the useful part when something goes wrong, because it separates three
different faults that otherwise look identical:

| Symptom | Means |
|---|---|
| Banner never says *listening for follows* | The subscription was rejected — scope or Client ID problem |
| Listening, but count stays at `0` | Twitch never sent the event — check you actually followed from a *different* account, and that it wasn't already following |
| Count goes up but no alert appears | The event arrived; the problem is in the page or its position in OBS |

If the second account already follows, unfollow and wait a moment before
refollowing — Twitch only sends `channel.follow` on a genuine new follow.

### Rehearsing subscriber alerts without any subscribers

You cannot make a real subscription happen on a channel that has none, and on
someone else's channel you cannot make one happen at all. The **Test sub / resub /
gift** buttons exist for that gap.

They are not mock-ups. Each one builds a genuine EventSub notification — the same
envelope and the same field names Twitch sends — and pushes it through exactly the
code a live event goes through. So a test covers the JSON parsing, the field names,
the tier and cumulative-month handling, the gift de-duplication and the alert
queue. It does **not** cover the websocket itself or Twitch's delivery.

**Test gift is the one worth clicking.** A gift bomb on Twitch is one gift event
*plus one `channel.subscribe` for every recipient*, so five gifted subs arrive as
six notifications. The test replays all six, and `/twitch/test?type=gift` reports
what happened:

```json
{"fired":"gift","injected":6,"alerts":1}
```

Six in, one out — the recipients are being swallowed rather than firing six alerts
on the stream. If that ever reads `"alerts":6`, the de-duplication has broken.

Test events deliberately leave no trace: they don't move the counts, don't become
the "latest subscriber", and aren't written to disk. Rehearse as often as you like
without leaving fake names on someone's overlay.

Goals left at `0` track the next round number above the current count, so the bar
always has somewhere to go without you editing a config file. Set a number to pin it.

### Styling them

Open `/customize` and use the tabs at the top — the alert page has **Test follow / sub /
resub / gift** buttons so you can style it without waiting for a real event. Every
option is a query parameter, so the URLs can also be hand-edited:

**Alerts** — `?style=slide|pop|bar` · `hold=6` (seconds on screen) ·
`followColor=1db954` · `subColor=a970ff` · `giftColor=ff9d3f` ·
`align=left|center|right` · `valign=top|middle|bottom` · `scale` · `radius` ·
`opacity` · `showMessage=0|1` · `demo=1`

**Followers & subs** — `?cycle=8` (seconds per face) · `transition=slide|fade|none` ·
`width=300` · `gap=12` · `followGoal=500` · `subGoal=50` · `showFollowers=0|1` ·
`showSubs=0|1` · `theme=glass|solid` · plus the same position, scale, radius,
opacity and colour options · `demo=1`

`demo=1` fills either page with sample data so you can position it in OBS before any
real event happens. Leave it off the URL you actually use.

### Behavior details

- Follows and subs arrive over an **EventSub websocket**, so alerts appear within a
  second. Totals are polled separately every 60s, because EventSub has no "the count
  changed" event.
- **Gift subs fire one alert, not twenty.** Twitch also sends a separate event for each
  recipient of a gift bomb; those are suppressed so the gifter is announced once.
- Alerts are **queued**, so two follows a second apart play in turn with a full hold
  each rather than overwriting one another.
- **The most recent subscriber only ever arrives as an event** — Twitch's subscriber
  API carries no timestamp, so there is nothing to sort by. It is remembered across
  restarts, but a brand-new setup shows only the goal face until someone subscribes.
- **Subscriptions are optional throughout.** A followers-only token, or a channel
  that isn't affiliate, fails every subscription-related request — and that is
  treated as "no sub data", never as a broken token. The subscriber box hides
  itself rather than showing a zero, and follow alerts are completely unaffected.
- An **expired token** is reported in the tray and the customizer instead of the
  counts silently freezing. User tokens expire — regenerate when that happens.
- The alerts page stays **completely invisible** when nothing is happening and when it
  cannot reach the server, so a connection problem never puts a box on your stream.

## Live equaliser

The bars next to "Now Playing" are a real spectrum analyser, not an animation.
The app captures whatever Windows is playing, runs an FFT over it, and streams the
frequency bands to the overlay about 30 times a second. **Left bars follow bass, right
bars follow treble** — kick drums punch the left, hi-hats and cymbals flicker on the right.

**It listens only to the player the overlay is showing.** If the card says Spotify, the
bars follow Spotify — a friend talking in Discord, a YouTube video, or a game will not
move them. The analyser follows the source picker automatically, so pinning a player
pins the equaliser too. Nothing to configure.

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

Per-app capture needs **Windows 11 or Windows 10 build 20348+**. On anything older it
falls back to capturing all system audio, which still works — other sounds just move the
bars too. `/spectrum.json` reports which mode is in use:

```
"status":"capturing Spotify.exe only (2ch 48000Hz)"
"status":"capturing all system audio (2ch 48000Hz)"
```

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
