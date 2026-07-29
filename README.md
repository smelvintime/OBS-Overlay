# Now Playing — Stream Overlay

A self-hosted alternative to Songplz. Shows the song you're currently listening to on your stream.

Reads Windows' own media session, so it works with **Spotify, Apple Music, iTunes, Tidal, and browser players** — with **no API keys, no OAuth, no account linking**. Whatever is playing on this PC is what shows up.

Nothing to install: it runs on the Windows PowerShell that ships with Windows.

## Quick start

1. Double-click **`Start-Overlay.bat`**. Leave the window open while streaming.
2. In OBS: **Sources → + → Browser**
3. Set **URL** to:
   ```
   http://127.0.0.1:8787/
   ```
4. Set **Width** `600`, **Height** `200`
5. Leave **"Shutdown source when not visible"** unchecked so it keeps updating.

That's it — play a song and the card appears. It updates automatically as tracks change.

## Customizing

Add options to the OBS Browser Source URL as query parameters:

```
http://127.0.0.1:8787/?accent=1db954&theme=glass&align=left
```

| Option | Values | Default | What it does |
|---|---|---|---|
| `accent` | hex, no `#` (e.g. `ff0055`) | `1db954` | Bar/glow/equalizer color |
| `theme` | `glass`, `solid`, `minimal` | `glass` | Card style. `minimal` = art + text, no card |
| `align` | `left`, `right` | `left` | Which side it sits and animates from |
| `scale` | e.g. `1.25` | `1` | Overall size multiplier |
| `radius` | px, e.g. `24` | `18` | Corner roundness |
| `showAlbum` | `0`, `1` | `0` | Show the album name line |
| `hideWhenPaused` | `0`, `1` | `0` | `1` hides the card while paused instead of dimming it |

Some combinations worth trying:

- Spotify green, bottom-left glass card — `?accent=1db954`
- Apple Music pink, right side — `?accent=fa2d48&align=right`
- Clean, no card chrome — `?theme=minimal&scale=1.2`
- Only visible while actually playing — `?hideWhenPaused=1`

## Using a different port

If port 8787 is taken, edit `Start-Overlay.bat` and change `-Port 8787`, then use the
matching port in the OBS URL.

## API (for chat bots / other tools)

| Endpoint | Returns |
|---|---|
| `GET /np` | JSON: `{playing, title, artist, album, app, id, hasArt}` |
| `GET /art?id=<id>` | Current album art image bytes (`204` if none) |
| `GET /` | The overlay page itself |

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

## Notes and limits

- **Local only.** The server binds to `127.0.0.1`, so it is not reachable from the internet
  and nothing is sent anywhere. Your listening data stays on this machine.
- **Reads the PC's media session**, so it reflects whatever is playing *on this computer*.
  If you play music from your phone, it won't appear.
- **Picking between apps:** if several things could be playing, it prefers a source that is
  actively playing, and prefers real music apps (Spotify/Apple Music/iTunes/Tidal) over a
  browser tab. So a paused YouTube tab won't hijack the overlay.
- If the card doesn't appear, confirm the launcher window is still open, and check
  `http://127.0.0.1:8787/np` in a browser to see what the server is reading.
