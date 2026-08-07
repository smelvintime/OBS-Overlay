# Extending the app

The app is built so a new page — an extra overlay, a new dashboard panel, an
experiment — plugs in as its own file without touching anything that already
works. This page is the contract.

## Adding a page is dropping a file

1. Create `mypage.html` in the repo root. Names are **lowercase letters,
   digits and dashes** only (the build refuses anything else, because the name
   becomes a URL).
2. Run `Build.bat`.

That's it. `build.ps1` embeds every `*.html` in the root into the exe, and the
server serves any embedded page at `/<name>` (so `mypage.html` → 
`http://127.0.0.1:8787/mypage`). No build-script edit, no server route, no
registration anywhere. Deleting the file removes the page just as cleanly.

The same discovery applies to the other two kinds of module:

| Drop a file in… | And it becomes… |
|---|---|
| repo root (`*.html`) | a page served at `/<name>` |
| `src\` (`*.cs`) | part of the build — new backend code compiles in automatically |
| `themes\` (`*.json`) | a built-in theme (listed by `/themes`, protected from user overwrite/delete) |

## What a page gets for free

Every page is a full standalone HTML document — its own CSS and JS, no shared
stylesheet to collide with. Include this in `<head>` to join the theme system:

```html
<script src="/shared.js"></script>
<script>if(window.NPO) NPO.themeBoot('source');</script>
```

Use `'source'` for pages OBS captures (polls for theme changes forever) and
`'dashboard'` for pages a human reads (paints once, no flash). Keep your own
`:root{}` defaults so the page still renders if `/themes` never answers.

Live data comes from the existing feeds — a new overlay normally needs **no
new server code at all**:

| Feed | What | How |
|---|---|---|
| `/np` | current track JSON | poll |
| `/art?id=…` | album art (cacheable per track) | `<img>` |
| `/spectrum` | equaliser bands ~30 fps | `NPO.stream('/spectrum', fn)` |
| `/twitch/events` | follows/subs/gifts as they happen | `NPO.stream('/twitch/events', fn)` |
| `/twitch` | totals, goals, latest follower/sub | poll |
| `/bot` | chat bot status | poll |
| `/prefs`, `/themes` | shared preferences, theme data | poll |

`NPO.stream` handles WebSocket-with-SSE-fallback and silent reconnection —
never open your own raw `EventSource`/`WebSocket` in a page, or you lose the
OBS six-connection protection that took a while to win.

Shared page furniture lives there too, so a new source speaks the house style
without copying it:

| Helper | Does |
|---|---|
| `NPO.socialMark(p)` / `NPO.socialList(qs, prefix)` | the platform logos and the `?handle=` vocabulary |
| `NPO.cornerList(qs)` | reads `?corners=`, defaulting to the house diagonal |
| `NPO.corners(host, list)` | hangs the accent brackets on `host` and cuts the matching corners |

`NPO.corners` needs a `position:relative` host that its page never empties,
and takes its look from custom properties on that host — `--cnr-c`,
`--cnr-size`, `--cnr-w`, `--cnr-off`, `--cnr-glow`, `--cnr-op`. Leave them
unset for the standard 18px accent bracket. Keep the page's own
`border-radius` rule as the shape to fall back to if `shared.js` never loads.

## Optional integrations — each one edit, each independent

None of these are required, and skipping them breaks nothing:

- **Dashboard tab** — add one line to the `TABS` array in `app.html`.
- **Customizer support** — add an entry to `TARGETS` in `customize.html`
  (defaults + controls; the generic machinery renders it and builds URLs).
- **Layouts comparison** — add a preview frame in `layouts.html`.
- **Tray shortcut** — add a menu line in `BuildTray()` in
  `src/NowPlayingOverlay.cs`.

## Rules that keep clients safe

These are the invariants the codebase defends; a new module must too.

- **Any route that writes state** (disk, settings, process) must check
  `SameOriginRequest(req)` and return `SendForbidden` otherwise. Every
  existing writer does; a drive-by web page can reach this server.
- **Persisted files** the app owns go in `%APPDATA%\NowPlayingOverlay`, never
  beside the exe (only `twitch-config.json` lives there, deliberately).
- **Never create the iTunes COM object unless the iTunes process is already
  running** — instantiating it launches iTunes.
- **Nothing binary gets committed.** The repo is public and distribution is
  source-only; a committed exe gets the whole download flagged by antivirus.
- Long-lived streams count against `MaxSpectrumClients` / `MaxTwitchClients`;
  reuse the existing feeds rather than adding new held-open connections.
