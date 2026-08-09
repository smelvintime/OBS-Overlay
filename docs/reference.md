# Reference

The tray icon, startup, ports, the HTTP API, and what to check when something
misbehaves.

## The tray icon

There's no window. Everything lives on the tray icon (a green ♫). **Right-click** it for:

- **Open dashboard…** — everything in one place: choosing a source, customizing,
  layouts, themes, Twitch. `http://127.0.0.1:8787/app`
- **Choose which player to follow…**, **Customize the overlay…**, **Compare layouts…**
  — shortcuts straight into that dashboard's matching tab
- **Preview overlay…** and **Twitch alerts and stats** — the raw OBS source pages themselves
- **Copy OBS browser source URL** — straight to the clipboard
- **Start with Windows** — tick to launch automatically at login
- **Open log folder** — where to look if something needs troubleshooting
- **Restart** — restarts the app in place, no hunting for the exe. Safe mid-stream:
  the overlays go quiet for a second or two and reconnect on their own.
  **It does not apply an update** — it relaunches the same exe that is already
  on disk, and Windows will not let that file be replaced while the app is
  running. To update, run `Build.bat` (it closes the app, rebuilds, restarts it)
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

## Using a different port

If port 8787 is taken, start the app with `NowPlayingOverlay.exe -port 8788`, then use
the matching port in the OBS URLs. A non-default port is remembered by **Start with
Windows**, so autostart keeps using it too.

## If it crashes

Every page retries forever on its own — the now-playing overlay refetches
every 1.5s, the dashboard's Twitch banner and source picker poll every few seconds, and
the alert/stats/equalizer streams reconnect on their own a few seconds after a drop.
None of them ever give up. So the moment the exe is back, whatever OBS already has open
picks the overlay back up **with nothing clicked in OBS** — same source, same URL, no
refresh needed.

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
entirely: the same page just sits there quietly retrying. **"Refresh browser when scene
becomes active"** is fine to leave on either way.

There's also a diagnostics page at **<http://127.0.0.1:8787/diag>** — build version,
what every music source reports, live connection counts, and the Twitch state, all on
one page. When two machines disagree, compare their `/diag` pages first.

## API (for chat bots / other tools)

| Endpoint | Returns |
|---|---|
| `GET /np` | JSON: `{playing, title, artist, album, app, source, id, hasArt}` |
| `GET /art` | Current album art image bytes (`204` if none) |
| `GET /spectrum` | Live frequency bands, ~30fps: WebSocket upgrade or SSE stream |
| `GET /spectrum.json` | One-shot spectrum snapshot, handy for debugging |
| `GET /sources` | Diagnostic: what each provider sees, which one won, current pin |
| `GET /themes` | Every theme the app knows (built-in + yours) and which is active |
| `GET /presets` | Your saved looks: `{presets:[{name, url, page, saved}]}` |
| `GET /setsource?mode=prefer\|only\|auto&app=<name>` | Change which player is followed |
| `GET /` | The overlay page itself |
| `GET /alerts` | Twitch follow/sub alert source |
| `GET /stats` | Twitch follower and subscriber boxes |
| `GET /twitch` | JSON: connection status, follower/sub totals, goals, most recent of each |
| `GET /twitch/events` | Follows and subs: WebSocket upgrade or SSE stream |
| `GET /twitch/test?type=follow\|sub\|resub\|gift&user=<name>` | Fire a fake alert, for styling without waiting for a real one |
| `GET /layouts` | Side-by-side layout previews |
| `GET /customize` | Full customizer with a live preview |
| `GET /control` | Source picker page |
| `GET /update/status` | JSON: this build's version/date, and the updater's progress while one runs |
| `GET /update/check` | Asks GitHub whether a newer commit exists (cached 30 min; `?force=1` re-asks). App pages only |
| `GET /update/run` | Downloads the latest source, rebuilds locally, swaps the exe and restarts. App pages only |
| `GET /league` | JSON: the League tracker's status, record, rank line and discovery state |
| `GET /league/path?value=<folder>` | Stores where League is installed, for when discovery needs telling (empty clears it). App pages only |

If the overlay ever shows the wrong thing, `/sources` is the fastest way to see why —
it reports the Windows media session and iTunes separately, plus which one was chosen.

The built-in Twitch bot uses `/np`.

## Building details

`Build.bat` runs `build.ps1`, which compiles with the C# compiler included in the
.NET Framework on every Windows machine — no Visual Studio, no .NET SDK, no
downloads. Output goes to `dist\`.

No compiled executable is ever published or committed here, on purpose: everyone
builds the same few-second way, and a binary you compiled yourself never came from
the internet, so antivirus reputation checks have nothing to object to.

Updating keeps that rule, whether you press the button or it happens by itself:
the newest *source* ZIP is downloaded from GitHub, the same build runs on your
machine, then the new exe is swapped in over the old one and the app restarts.
If any step fails, the running app is left untouched and the reason appears in
the header and the log. The old exe is kept beside the new one as
`NowPlayingOverlay.exe.old` until the next successful start, as a just-in-case
copy.

Automatic updates check every four minutes and install at the first quiet
moment. Quiet means two things, both asked rather than assumed: Twitch says the
channel is not broadcasting, and the League client is not in champ select or a
game. If Twitch cannot be asked — not connected, API down — an attached OBS
browser source counts as "something is on a canvas somewhere" and it waits.
A failed attempt backs off for an hour rather than retrying every four minutes.

The commit installed is written to `prefs.txt` as `updateSha`. That is what
stops a machine whose clock runs behind GitHub's from reinstalling the same
commit forever: the date test alone would read every build stamp as older than
every commit, which for a button is a wasted click and for something unattended
is a restart loop.

## Notes and limits

- **Local only.** The server binds to `127.0.0.1`, so it is not reachable from the
  internet and nothing is sent anywhere. Your listening data stays on this machine.
- **Reads this PC**, so it reflects whatever is playing *on this computer*. Music from
  your phone won't appear.
- If the card doesn't appear, check the tray icon is present, and open
  `http://127.0.0.1:8787/np` in a browser to see what the server is reading.
