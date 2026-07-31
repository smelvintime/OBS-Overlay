# Now Playing — Stream Overlay

A now-playing music overlay for OBS, with Twitch alerts, goal boxes, a theme
system and a `!song` chat bot. **One file, no install, a guided setup page for
everything else.**

Works with **Spotify, Apple Music, iTunes, Tidal and browser players** — whatever
is playing on this PC is what shows up, with no API keys or account linking for
the music side at all.

## What's in the box

- 🎵 **Four overlay layouts** — card, big, full-width ticker, minimal — with a
  **live equaliser** driven by the actual audio of the player being shown
- 🔔 **Twitch alerts and goal boxes** — follows, subs, resubs, gifts; goal bars
  with a most-recent face; set up by a **built-in wizard** with a Connect-with-Twitch
  button, and the connection **renews itself forever**
- 🎨 **Themes** — recolour the whole app and every OBS source in one click,
  build your own in a visual editor, and trade them as small files
- 🎛️ **A customizer** with live preview for every option, plus **saved looks**
  so a finished design is never lost in an OBS URL field again
- 💬 **A `!song` chat bot** that answers viewers with the current track —
  set up with one click, same as the alerts
- 📦 **One ~400 KB exe.** Local only (`127.0.0.1`), nothing sent anywhere.

## Get it

1. **Download the source** — green **Code** button above → **Download ZIP**.
2. **Unzip it** anywhere.
3. **Double-click `Build.bat`.** Windows already has everything it needs; a few
   seconds later `NowPlayingOverlay.exe` is in the `dist\` folder.
4. **Move that file somewhere permanent and run it.** Permanent matters: your
   settings are saved in a file next to the exe, so running it from a new folder
   looks like a first run. To update later, build the new version and copy the
   new exe over the old one in place — settings carry over untouched.

There is no ready-made download on purpose. An unsigned program from the internet
gets flagged by antivirus reputation checks no matter what's in it; a file you
compile on your own machine never came from the internet, so there is nothing to
object to. Building takes seconds and needs nothing installed — the compiler ships
inside Windows.

> **First run of your built exe:** Windows may still show the blue SmartScreen
> banner (*"Windows protected your PC"*) because the file has no publisher
> signature — **More info → Run anyway**, once. To be clear about what the app
> does that a scanner might notice: it writes the `Run` registry key **only** when
> you tick *Start with Windows*, captures system audio **only** for the live
> equaliser, opens a listening socket because it **is** a local web server for
> OBS, and reads the process list to find your music player. All of it is in the
> source you just downloaded, and the server is unreachable from outside your
> machine.

## Quick start

1. **Run it.** No window appears — it lives in the **system tray** (bottom-right,
   next to the clock, sometimes behind the **^** arrow).
2. **Set up Twitch** (optional) — a setup page opens on first run and walks you
   through it, including a **Connect with Twitch** button that handles sign-in.
   No terminal, no tokens to paste. Skip it and the music overlay still works.
3. **In OBS**: **Sources → + → Browser**, URL `http://127.0.0.1:8787/`, size
   `600 × 200`, and leave *"Shutdown source when not visible"* unchecked.

Play a song and the card appears. It updates by itself from then on.

## The dashboard

Everything lives at **<http://127.0.0.1:8787/app>** (or right-click the tray
icon → **Open dashboard…**):

| Tab | What it does |
|---|---|
| **Choose source** | Pin the overlay to one player, or leave it automatic |
| **Layouts** | All four overlay styles side by side |
| **Customize** | Every look option with live preview, copy the URL, save looks |
| **Saved looks** | Every look you saved, ready to copy back into OBS |
| **Themes** | Apply, build, import and export whole-app colour themes |
| **Chat bot** | Set up the `!song` bot with one click, switch it on, manage its commands |
| **How to use** | The same guidance, inside the app |

## Digging deeper

| Guide | Covers |
|---|---|
| [The music overlay](docs/overlay.md) | Layouts, every customizer option, the equaliser, choosing which player to follow, how music is detected |
| [Twitch](docs/twitch.md) | The setup wizard, alerts and goal boxes, styling, testing without real events, the chat bot, manual setup |
| [Themes](docs/themes.md) | Switching, building your own, sharing theme files |
| [Reference](docs/reference.md) | Tray menu, autostart, ports, crash recovery, the HTTP API |
| [Extending](docs/extending.md) | For tinkerers: adding your own pages, overlays and themes — each plugs in as a single dropped file |

## License

[MIT](LICENSE) — free to use, modify and redistribute, including commercially.
Provided as-is, with no warranty.
