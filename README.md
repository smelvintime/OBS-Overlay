# Now Playing — Stream Overlay

A now-playing music overlay for OBS, with Twitch follower and subscriber alerts,
goal boxes and a `!song` chat bot. **One file, no install, nothing to configure by hand.**

Works with **Spotify, Apple Music, iTunes, Tidal and browser players** — whatever is
playing on this PC is what shows up, with no API keys or account linking for the music
side at all.

### ⬇️ [Download the latest release](https://github.com/smelvintime/OBS-Overlay/releases/latest)

One file: `NowPlayingOverlay.exe`. Every overlay page is built into it, so there is
nothing else to download and nothing to unzip. You do **not** need to clone this
repository to use the overlay — that's only for building it yourself.

## Quick start

1. **Run it.** The first time, it asks where you'd like to keep it and offers a
   desktop/Start-menu shortcut, so it doesn't end up lost in Downloads. It copies
   itself there and starts from its new home. After that no window appears: it lives
   in the **system tray** (bottom-right, next to the clock).
2. **Set up Twitch** (optional) — a setup page opens on first run and walks you
   through it, including a **Connect with Twitch** button that handles sign-in for
   you. No terminal, no tokens to paste, and it renews itself afterwards. Skip this
   and the music overlay still works.
3. **In OBS**: **Sources → + → Browser**, set the URL to `http://127.0.0.1:8787/`,
   size `600 × 200`, and leave *"Shutdown source when not visible"* unchecked.

That's it — play a song and the card appears. It updates automatically as tracks change.

Everything else — customizing how it looks, comparing layouts, choosing which player to
follow, Twitch alerts — lives in one dashboard: **<http://127.0.0.1:8787/app>** (or
right-click the tray icon → **Open dashboard…**). The individual pages it's built from
(`/customize`, `/layouts`, `/control`) still work as direct links too, if you'd rather
bookmark one tab on its own.

It's a single ~390 KB file. Nothing to install, no PowerShell, no runtime download: it
uses the .NET Framework already present on every Windows 10/11 machine, and the overlay
pages are embedded inside the executable.

## Antivirus and SmartScreen warnings

Expect one, and here is the honest reason why.

The download is **not code-signed** — a certificate costs a few hundred dollars a
year — so Windows has no publisher to show you and falls back on reputation. A brand
new file that almost nobody has downloaded scores badly on that alone, which is what
produces *"Windows protected your PC"* (**More info → Run anyway**) and browser
warnings that a file "isn't commonly downloaded".

Some scanners go further and flag it outright. That is a **false positive**, but it
is not a stupid one, because this app genuinely does several things that malware also
does:

| What it does | Why it looks bad | Why it's here |
|---|---|---|
| Copies itself to a folder you choose and runs the copy | droppers install themselves this way | the first-run "where should this live?" step |
| Writes to the `Run` registry key | that is how malware persists across reboots | the **Start with Windows** option, which you tick yourself |
| Captures system audio | eavesdropping | the live equalizer follows real audio |
| Opens a listening socket | backdoors listen for connections | it *is* a web server — that is how OBS reads the overlay |
| Reads the list of running processes | malware hunts for what to attack or avoid | to find your music player |

Every one of those is visible in the source in this repository, and the server binds
to `127.0.0.1` only, so nothing it serves is reachable from outside your machine.

**Verifying you got the real file.** Each release lists the SHA-256 of its
executable. On the downloaded file:

```
certutil -hashfile NowPlayingOverlay.exe SHA256
```

If it matches the release, the file is exactly what was published here.
For `v0.9.0`: `F112D78F3079CB7CF34082D0596C8CF07A7B69F683591EF5C109157E534805BB`

If your scanner quarantines it and you want it back, the fix is to report the false
positive to your antivirus vendor (Microsoft's form is at
<https://www.microsoft.com/en-us/wdsi/filesubmission>) rather than to disable your
antivirus. If you would rather not run an unsigned executable at all, that is a
completely reasonable call — you can build it yourself from source with `build.ps1`
and get a binary that never touched the internet.

Use a different port with `NowPlayingOverlay.exe -port 8788`.

## The tray icon

There's no window. Everything lives on the tray icon (a green ♫). **Right-click** it for:

- **Open dashboard…** — everything below, in one place: choosing a source,
  customizing, layouts, Twitch. `http://127.0.0.1:8787/app`
- **Choose which player to follow…**, **Customize the overlay…**, **Compare layouts…**
  — shortcuts straight into that dashboard's matching tab
- **Preview overlay…** and **Twitch alerts and stats** — the raw OBS source pages themselves
- **Copy OBS browser source URL** — straight to the clipboard
- **Start with Windows** — tick to launch automatically at login
- **Open log folder** — where to look if something needs troubleshooting
- **Exit** — this is how you stop it

**Double-click** the icon to jump to the dashboard. Hovering shows the current
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

## If it crashes

Every page here already retries forever on its own — the now-playing overlay refetches
every 1.5s, the dashboard's Twitch banner and source picker poll every few seconds, and
the alert/stats/equalizer streams reconnect on their own a few seconds after a drop. None of them ever
give up. So the moment the exe is back, whatever OBS already has open picks the overlay
back up **with nothing clicked in OBS** — same source, same URL, no refresh needed.

The exe also tries to relaunch itself. If it crashes, it attempts to start a fresh copy
with the same port and settings, up to 5 times. It deliberately does **not** retry a crash
that happens in the first 10 seconds of starting — that almost always means something
will just crash again immediately (a bad config, a port fight), and retrying forever
would spin instead of actually recovering. If you ever see the tray icon gone and it
hasn't come back, that's what happened; check the log (tray icon → **Open log folder**,
or `%APPDATA%\NowPlayingOverlay\app.log`) for what it hit and start it by hand.

**The one thing that's a genuine OBS setting, not this app:** if a Browser Source has
**"Shutdown source when not visible"** turned on, OBS destroys that page whenever the
scene isn't active and loads it fresh next time — if that reload happens to land while
the exe is mid-relaunch, the source can show blank until you switch scenes again or
right-click it and choose **Refresh**. Leaving that setting **off** avoids the gap
entirely: the same page just sits there quietly retrying, exactly as described above.
**"Refresh browser when scene becomes active"** is fine to leave on either way.

## The old PowerShell version

An earlier version of this project was a pair of PowerShell scripts run from `.bat`
files. They have been retired to [`legacy/`](legacy/) and are kept for reference only:
they have no Twitch alerts, no goal boxes, no setup wizard, no tray icon and no live
equalizer, and they are not maintained. [`legacy/README.md`](legacy/README.md) lists
the differences in full.

Use the `.exe` — that is the project now.

## Building the .exe yourself

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Compiles with the C# compiler included in the .NET Framework on every Windows machine —
no Visual Studio, no .NET SDK, no downloads. Output goes to `dist\`.

`dist\NowPlayingOverlay.exe` is committed so the repository always builds to something
runnable, but it is a **development build** — for normal use take the one attached to
the [latest release](https://github.com/smelvintime/OBS-Overlay/releases/latest), which
is a known, tagged version.

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
| `mqspeed` | px/sec, `5`–`200` | `30` | How fast an overflowing title scrolls back and forth (card, big, minimal) |
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

> **Long titles:** a title that doesn't fit its box scrolls back and forth
> automatically (`mqspeed` sets how fast). This only works when the page itself
> knows the text is clipped — if you **crop or scale down the source in OBS**
> instead of sizing the Browser Source to match, the page believes everything
> fits and nothing scrolls. Size the source right and let `scale=` do the shrinking.

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

If port 8787 is taken, start the app with `NowPlayingOverlay.exe -port 8788`, then use
the matching port in the OBS URLs. A non-default port is remembered by **Start with
Windows**, so autostart keeps using it too.

## API (for chat bots / other tools)

| Endpoint | Returns |
|---|---|
| `GET /np` | JSON: `{playing, title, artist, album, app, source, id, hasArt}` |
| `GET /art` | Current album art image bytes (`204` if none) |
| `GET /spectrum` | Live frequency bands, ~30fps: WebSocket upgrade or SSE stream (.exe only) |
| `GET /spectrum.json` | One-shot spectrum snapshot, handy for debugging (.exe only) |
| `GET /sources` | Diagnostic: what each provider sees, which one won, current pin |
| `GET /setsource?mode=prefer\|only\|auto&app=<name>` | Change which player is followed |
| `GET /` | The overlay page itself |
| `GET /alerts` | Twitch follow/sub alert source (.exe only for live data) |
| `GET /stats` | Twitch follower and subscriber boxes (.exe only for live data) |
| `GET /twitch` | JSON: connection status, follower/sub totals, goals, most recent of each |
| `GET /twitch/events` | Follows and subs: WebSocket upgrade or SSE stream (.exe only) |
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

2. **Open `twitch-config.json`** next to the app — the setup wizard creates it, or copy
   `twitch-config.example.json` if you skipped that.

3. **Fill in the chat bot's half:**
   ```json
   {
     "channel": "your_channel",
     "botUsername": "your_bot_or_own_username",
     "oauthToken": "oauth:abcd1234..."
   }
   ```
   The token must keep the `oauth:` prefix. You can use your own account as the bot,
   or a separate account if you want a distinct bot name in chat.

4. **Restart the app, then switch the bot on** in the dashboard's **Chat bot** tab
   (<http://127.0.0.1:8787/app#bot>). That page shows whether it connected, lists the
   commands and lets you add your own.

The bot runs inside the app — there is no second window to keep open.

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

Both need `NowPlayingOverlay.exe`; the retired PowerShell version cannot serve them.

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

### Not expiring every few hours

A Twitch user token like the one above is short-lived - a stream left running
unattended will eventually see it expire, and the tray/customizer will say the
token was rejected and needs regenerating.

To make the app renew it on its own instead, add two more fields:

```json
{
  "channel": "your_channel",
  "clientId": "your_client_id",
  "clientSecret": "your_client_secret",
  "apiToken": "your_user_token",
  "refreshToken": "your_refresh_token",
  "followerGoal": 0,
  "subGoal": 0
}
```

- **`clientSecret`** - from the same app you registered in step 1, on its page in
  the [Developer Console](https://dev.twitch.tv/console).
- **`refreshToken`** - printed right alongside the access token by the same
  `twitch token -u -s "..."` command from step 2.

With both present, a rejected token is refreshed automatically the moment it's
needed - no restart, no manual regenerate - and `twitch-config.json` is
rewritten in place with the new `apiToken` and `refreshToken` each time (Twitch
issues a fresh refresh token on every use and invalidates the old one, so this
file has to stay in sync with whichever one is current). Leave both blank and
nothing changes: the original regenerate-by-hand behaviour is exactly what you
had before.

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

Open `/customize` and use the tabs at the top — both the alert page and the followers &
subs page have **Test follow / sub / gift** buttons so you can style them without waiting
for a real event. (On the followers & subs tab those do nothing unless **Break the cycle
and announce it here** is switched on — there is nothing for them to trigger otherwise.)
Every option is a query parameter, so the URLs can also be hand-edited:

**Alerts** — `?style=slide|slash|pop|bar` · `hold=6` (seconds on screen) ·
`frame=blade|twin|soft` · `slant=0|1` · `followColor=1db954` · `subColor=a970ff` ·
`giftColor=ff9d3f` · `align=left|center|right` · `valign=top|middle|bottom` ·
`scale` · `radius` · `opacity` · `demo=1`

**Followers & subs** — `?cycle=8` (seconds per face) · `transition=slide|slash|fade|none` ·
`mode=pair|rotate` · `order=subs|followers` · `arrange=stack|row` ·
`takeover=0|1` · `hold=6` · `frame=blade|twin|soft` · `slant=0|1` · `mark=2` ·
`width=300` · `gap=12` · `followGoal=500` · `subGoal=50` · `showFollowers=0|1` ·
`showSubs=0|1` · `theme=glass|solid` · plus the same position, scale, radius,
opacity and colour options · `demo=1`

`demo=1` fills either page with sample data so you can position it in OBS before any
real event happens. Leave it off the URL you actually use.

**Slash** is the arrival built for the Zed look: a long slow cut across the canvas
with the shuriken spinning a turn and a half and coasting to a stop, then the whole
thing retraced on the way back out. The other three arrivals are short nudges and
leave the mark still.

It is also available as `transition=slash` on the followers & subs boxes, so the
ordinary cycle between goal and most-recent uses the same cut. It runs close to a
second each way, so give it a longer `cycle=` than you would slide or fade — at
`cycle=3` the box spends most of its time animating.

**`slant=0`** squares off every diagonal — the tapered blades, their lit edges and the
goal bar. Scenes are rectangles, and the lean can fight the layout around it.

**`mark`** scales the shuriken on the goal box labels, where `1` is the original size
and `2` (the default) is twice that.

### Two ways to show followers and subs

`mode=pair` is the original: both boxes on screen together, each flipping between its
own goal and its own most recent name.

`mode=rotate` uses **one panel in one spot** and works through all four in turn — goal,
most recent, then the other box, and round again. `order=` picks which box leads and
`cycle=` sets how long each face holds. A channel that can't expose subscriptions drops
those two phases rather than cycling to blank ones.

`arrange=row` puts the two boxes side by side instead of stacked. It has nothing to
arrange in rotate mode, where only one is ever up.

### Announcing events in the goal box

`takeover=1` makes the goal boxes and the alerts one thing. Instead of the box cycling
on regardless while an alert appears somewhere else, the box the event belongs to breaks
off its cycle, slashes the name in over its own spot with the shuriken spinning, holds
for `hold=` seconds and then goes back to the standby faces.

It plays in the box's own frame and colours rather than dropping the alert card in, so
it reads as the box reacting rather than as a second overlay landing on top of it.

Two things worth knowing:

- **If you also run the Twitch alerts source, every event shows up twice** — once in the
  alert and once in the goal box. That is inherent to running both; pick one, or place
  them so the repetition looks deliberate.
- Events **queue**, one at a time across both boxes, for the same reason the alerts page
  queues them: a gift bomb that also lands a follow would otherwise animate two panels
  against each other in the same stack.

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

## License

[MIT](LICENSE) — free to use, modify and redistribute, including commercially.
Provided as-is, with no warranty.
