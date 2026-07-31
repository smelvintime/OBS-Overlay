# The music overlay

Everything about the now-playing card: layouts, customizing, the equaliser,
and how the app decides what you're listening to.

## Layouts

Open **<http://127.0.0.1:8787/layouts>** (dashboard → **Layouts**) to see all four
side by side against a mock stream background. Each one has an **opacity slider**
and a **new-song transition** picker that write straight into the URL you copy,
plus a **Preview** button so you can watch the transition without skipping a track.

| Layout | URL | OBS size | What it is |
|---|---|---|---|
| **Card** (default) | `/?layout=card` | 600 × 200 | Album art, title, artist in a frosted card |
| **Big** | `/?layout=big` | 900 × 320 | Large art and type, cover art blurred behind it |
| **Ticker** | `/?layout=ticker` | 1920 × 64 | Full-width bar with continuously scrolling text |
| **Minimal** | `/?layout=minimal` | 600 × 120 | No card at all — accent bar, art and shadowed text |

Ticker sits nicely along the very top or bottom edge; pair it with `valign=top`.
Big suits a dedicated music scene or a "starting soon" screen.

The same page has a **Twitch sources** section below the layouts — see
[Twitch alerts and goal boxes](twitch.md).

## Customizing

Open **<http://127.0.0.1:8787/customize>** (or right-click the tray icon →
*Customize the overlay*). Every option below has a control there, with a live
preview of whatever is playing right now, a background switcher so you can
check it stays readable over gameplay, and a **Replay song change** button.
Tabs at the top switch between the three OBS sources — the now-playing overlay,
the Twitch alerts, and the follower/subscriber boxes.
Copy the URL from the bar at the bottom when it looks right. Your choices are
remembered in that browser, and **Reset all** puts everything back.

### Saved looks

When a look is finished, press **Save look**, give it a name, and it lands in
the **Saved looks** list just above the URL bar — with Copy, Open and Delete
buttons. The list is kept by the app itself, not the browser, so a look you
save today is still there next month, in any browser, ready to paste back
into OBS. (An OBS source's URL field is technically also a place to store a
URL, but nobody has ever found one there again.)

### Every option

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

If audio capture is unavailable it falls back to the canned animation on its own, so the
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

**<http://127.0.0.1:8787/control>** (dashboard → **Choose source**)

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

From the command line:

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
