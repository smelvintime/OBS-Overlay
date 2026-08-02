# Twitch: alerts, goal boxes and the chat bot

Everything Twitch is optional — the music overlay works without any of it.

## The five-minute way: the setup wizard

The app opens **<http://127.0.0.1:8787/setup>** on its own the first time it runs
(also in the tray menu under **Twitch alerts and stats → Set up Twitch…**). It walks
you through the whole thing:

1. Your channel name, plus a Client ID and Secret from a Twitch app it shows you
   how to create — with pictures, and a copy button for the one URL Twitch needs.
2. A **Connect with Twitch** button that handles sign-in in your browser. No
   terminal, no tokens to paste anywhere.
3. **Test follow / sub** buttons so you see an alert fire before any real one exists.
4. The three URLs to put in OBS.

Because the wizard stores the Client Secret alongside the tokens, the connection
**renews itself indefinitely** — no expiring after a few hours, nothing to redo.

That's the whole setup. The rest of this page is what you get, how to style it,
and a by-hand route for people who'd rather not use the wizard.

## What you get

Two browser sources, separate from the now-playing card so they can be
positioned independently:

| Source | URL | What it does |
|---|---|---|
| **Alerts** | `/alerts` | Fires when someone follows, subscribes, resubscribes or gifts. Invisible between events, so it can cover the whole canvas. |
| **Followers & subs** | `/stats` | Two boxes, each alternating between who was most recent and a progress bar to the next goal. |

Goals left at `0` track the next round number above the current count, so the bar
always has somewhere to go without you configuring anything. Set a number in
`twitch-config.json` (`followerGoal`, `subGoal`) to pin it.

## Styling them

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

## Sounds and clips on alerts

Alerts can play a sound and show a gif, image or looping video each time one lands.

1. **Right-click the tray icon → Open media folder** and drop your files in:
   `mp3` / `wav` / `ogg` / `m4a` for sounds, `gif` / `png` / `jpg` / `webp` /
   `mp4` / `webm` for visuals.
2. In the **Customize** tab, pick the alerts source — the **Sound and clip**
   section lists whatever is in the folder. Pick, set the volume, size and
   place the clip, copy the URL into OBS as usual.

Details worth knowing:

- **The clip restarts on every alert.** A gif or video begins at its first
  frame the moment the alert lands — the file is held ready in memory, so
  there is no loading pause and it never appears mid-loop.
- **Place it anywhere.** The two position sliders pin the clip's centre to any
  point of the canvas — a corner, an edge, over the game. Dead centre is just
  the default, not the rule.
- **Clip delay** holds the visual back a beat after the alert lands, for a
  sound that builds up before the drop. Keep it shorter than the on-screen
  time, or the clip never gets its turn.
- **Loop it or play it once.** By default the clip repeats for as long as the
  alert is up. Switch **Loop the clip** off and it plays through a single
  time, then fades out on its own while the card finishes — even for gifs,
  whose loop instruction lives inside the file itself (the app edits its
  in-memory copy so one pass really is one pass).
- **Videos always play muted**; the sound comes from the separate sound
  setting, so the two can be mixed freely.
- **Hearing it in OBS**: browser sources output audio through OBS. If you don't
  hear it, check the source's *Control audio via OBS* setting and the Audio
  Mixer — the browser source has its own fader there.
- **The dashboard preview may stay silent** until you click somewhere on the
  page once. That is a browser autoplay rule, not a fault; OBS has no such rule
  and plays it every time.
- Keep clips short and reasonably sized — a gif is held decoded in memory for
  instant restarts, and a multi-hundred-MB video as an alert clip will stutter.
  A few seconds and a few MB is the sweet spot.

## Testing it with a second account

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

## Rehearsing subscriber alerts without any subscribers

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

## Behaviour details

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
  counts silently freezing — and with the wizard's setup it refreshes itself
  before you ever see that.
- The alerts page stays **completely invisible** when nothing is happening and when it
  cannot reach the server, so a connection problem never puts a box on your stream.

## The `!song` chat bot

Lets viewers type `!song` in chat and get the current track as a reply. The bot runs
inside the app — no second window — and speaks as **its own Twitch account** (or yours,
if you prefer), so it has its own sign-in.

1. **Open the dashboard's Chat bot tab** (<http://127.0.0.1:8787/app#bot>) and press
   **Set up the bot**. It signs in through the same Twitch app the main wizard
   registered, so do that setup first if you haven't.
2. **Sign in as the account the bot should speak as** when Twitch asks — a dedicated
   bot account, or your own if you want replies to come from you. If Twitch shows
   the wrong account, use its "Not you?" link first. The app learns the username
   from Twitch itself and stores everything, including a refresh token, so the
   bot's sign-in renews on its own from then on.
3. **Switch the bot on** in the same tab. It shows whether it connected, lists the
   commands, lets you add your own or delete any you don't want, and names the
   exact problem if something is wrong. Deleted a default by mistake? **Restore
   default commands** re-adds whatever is missing without touching the ones you
   kept or edited.

### Setting the bot up by hand instead

A hand-made token still works. Get one with the `chat:read` and `chat:edit` scopes,
logged in as the account you want speaking in chat — the official
[Twitch CLI](https://dev.twitch.tv/docs/cli/) route is
`twitch token -u -s "chat:read chat:edit"` — then fill in the chat half of
`twitch-config.json` and restart the app:

```json
{
  "botUsername": "your_bot_or_own_username",
  "oauthToken": "oauth:abcd1234..."
}
```

The token must keep the `oauth:` prefix. Note that hand-made tokens expire and don't
renew themselves — the **Set up the bot** button stores a refresh token alongside, so
that version heals itself when Twitch rotates it.

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

### Bot behaviour

- Ignores its own messages, so it can't loop.
- `\n` typed in a command's reply text splits it into separate chat messages,
  up to five - Twitch chat has no real line breaks, so several messages is what
  a multi-line reply means there. The same works in the `!song` templates.
- `!songrequest` and similar won't trigger it — only `!song` exactly, or `!song <args>`.
- Reconnects automatically with backoff if the connection drops.
- Twitch silently drops an identical repeated message within 30s, so repeat answers get
  an invisible character appended to stay visible.
- If the overlay server isn't running, it replies with a clear message rather than
  going silent.

### If the bot seems deaf or mute

Three things trip up nearly every fresh bot account, and none of them are bugs:

- **Verify the bot account's email first.** Twitch silently drops every message
  from an unverified account — the bot believes it sent the reply, chat never sees
  it. When Twitch drops a message it usually says why in a server notice, which the
  Chat bot tab shows on its banner, but a brand-new account should just verify its
  email before anything else.
- **Test commands from a different account.** The bot ignores messages from its own
  account (that's the anti-loop protection), so typing `!song` while logged in as
  the bot will never get an answer. Use your main account or ask a viewer.
- **Changing the bot account's security settings signs the bot out.** Enabling
  two-factor authentication (or changing the password) makes Twitch revoke the bot's
  tokens — the tab will report an authentication failure. Nothing is broken: press
  **Reconnect** on the Chat bot tab and it's back in one click.

## League game stats in chat

The bot can read results straight from the **League client running on the
streaming PC** — no Riot API key, no account linking, nothing to sign into.
It connects by itself whenever the client is open.

- **`!record`** (also `!last5`, `!wl`) answers with the last game and the
  recent record: *"Last game: Victory (12/3/8) - past 5: W W L W L"*.
- **Announce when a game ends** posts that line automatically as the game
  finishes (on by default; switch it on the bot tab's Game stats panel).
- **Repeat on a timer** re-posts it every 5/10/15 minutes — but only when a
  new game has finished since it last spoke, so it never spams an unchanged
  record.

The panel on the bot tab shows what it is currently watching and the exact
line it would say. If it shows "waiting for the League client", start League —
there is nothing else to configure. The League client's local data service is
unofficial, so a big League patch could interrupt this feature until an app
update; if that happens the panel says so and everything else keeps working.

## Setting it up by hand instead

If you'd rather not use the wizard, everything it does can be done manually.

1. **Register an app** in the [Twitch Developer Console](https://dev.twitch.tv/console)
   and note its **Client ID** (not secret) and **Client Secret**.

2. **Get a user token for your own account.** Only one scope is actually required:

   | Scope | Needed for | Required? |
   |---|---|---|
   | `moderator:read:followers` | Follower alerts, count and latest follower | **Yes** |
   | `channel:read:subscriptions` | Subscriber alerts and count | Optional |

   Via the [Twitch CLI](https://dev.twitch.tv/docs/cli/):
   ```
   twitch token -u -s "moderator:read:followers channel:read:subscriptions"
   ```

   A followers-only token is a fully supported setup, not a degraded one. The
   subscriber box hides itself, sub alerts simply never fire, and follows work
   exactly as they would otherwise.

3. **Put everything in `twitch-config.json`** next to the app (it is read from the
   exe's folder or up to three levels above, so `dist\` works):
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
   `clientSecret` and `refreshToken` are what make the token renew itself instead
   of expiring after a few hours — the refresh token is printed right alongside the
   access token by the same `twitch token` command. With both present, a rejected
   token is refreshed automatically the moment it's needed, and the file is
   rewritten in place with the new pair each time (Twitch issues a fresh refresh
   token on every use and invalidates the old one). Leave them out and you're
   choosing to regenerate by hand whenever the token expires.

4. **Restart the app.** The tray menu's *Twitch alerts and stats* submenu shows the
   connection state, and `/customize` shows a banner explaining any problem.

> **The most common mistake:** using a Client ID that did not issue the token. If you
> used a generator site rather than your own app, you must use *that site's* Client ID.
> Twitch checks the pair and rejects every request otherwise — which shows up as
> `Twitch rejected the token (401)`.

## Keeping the tokens safe

`twitch-config.json` holds a token that can post to chat as the bot account, and one
that can read your follower and subscriber lists. It's listed in `.gitignore` so it
won't be committed — keep it that way, and regenerate a token in Twitch if it ever
leaks.
