# Themes

A theme recolours **everything at once** — the dashboard pages and all three
OBS sources — where the [customizer options](overlay.md#customizing) style one
source at a time. Two ship built in: **Shockblade** (the electric blue) and
**Shadow** (near-black with a crimson edge). Open the **Themes** tab in the
dashboard to see them all as cards.

To switch: click a card, check the live preview, press **Apply**. Every OBS
source picks the change up within about five seconds — you never touch OBS.

## Making your own

1. In the **Themes** tab, press **New theme** (or **Make a copy** on any card
   to start from a look you like).
2. Give it a name and pick colours. The top row is what OBS shows — accent,
   alert colours, card and text. The bottom row is the dashboard itself.
   Everything lands in the live preview as you pick.
3. Press **Save theme**. It appears as a card marked **Yours**, and Apply
   works on it like any other.

You choose eight dashboard colours; the app derives the fiddly supporting
shades (input fields, hover states, hairlines) from them so the result always
looks deliberate.

## Sharing themes

**Export** on any of your cards downloads the theme as a small `.json` file —
send it to anyone. They press **Import a theme file**, pick it, done. Themes
are plain data: colour values only, checked by the app on the way in, so a
theme file from a stranger can change colours and nothing else.

Your themes live as files in `%APPDATA%\NowPlayingOverlay\themes`. Dropping a
`.json` in there by hand works too — it shows up on the next visit to the
Themes tab, no restart.

## What a theme file looks like

```json
{
  "name": "emerald",
  "label": "Emerald",
  "swatch": ["#3ddc84", "#0b2418"],
  "dashboard": { "--bg": "#07130c", "--accent": "#3ddc84" },
  "source":    { "--accent": "#3ddc84", "--text": "#eefff5" }
}
```

`dashboard` colours these pages; `source` colours what OBS shows. Any variable
left out keeps its default, so a theme can be as small as one accent colour.
`name` is lowercase letters, digits and dashes, and doubles as the filename.

## Pinning one source

One OBS source can be pinned to a theme on purpose, no matter what the app is
set to, by adding `?theme=<name>` to its URL. It still follows edits to that
theme's colours — it just ignores the app-wide switch.
