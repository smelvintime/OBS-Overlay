# Legacy PowerShell version

**These are kept for reference only. Use
[`NowPlayingOverlay.exe`](https://github.com/smelvintime/OBS-Overlay/releases/latest)
instead.**

Before the app existed, the overlay was a pair of PowerShell scripts you started
from `.bat` files and left running in two console windows. They still work, and
the now-playing card they serve still looks right, which is exactly why they are
a trap: they have not kept up, and nothing warns you at the time.

What you would be missing:

| | The `.exe` | These scripts |
|---|---|---|
| Twitch follow/sub alerts | yes | **no** |
| Follower/subscriber goal boxes | yes | **no** |
| Setup wizard and Twitch sign-in | yes | **no** — config edited by hand |
| Automatic token refresh | yes | **no** — expires every few hours |
| Live audio equalizer | yes | no, canned animation only |
| Customizer with live preview | yes | partial |
| Runs in the system tray | yes | no, two console windows |
| Start with Windows | yes | no |
| Survives its own crash | yes | no |
| WebSocket streams | yes | n/a |

The scripts also predate the fix for OBS starving its own browser sources, so
running several overlays against `server.ps1` can leave them frozen on screen.

They are still here because they are readable end to end in a text editor, which
makes them a reasonable thing to audit or borrow from. They are not maintained,
and fixes made to the app are not backported.

- `server.ps1` — the overlay web server. `Start-Overlay.bat` launches it.
- `twitch-bot.ps1` — the `!song` chat bot. `Start-TwitchBot.bat` launches it.
  Superseded by the bot built into the app, which the dashboard configures.

`diagnose.ps1` in the repository root is **not** legacy: it inspects what Windows
itself reports about media sessions, which is useful precisely when the app will
not start and its own `/diag` page cannot be reached.
