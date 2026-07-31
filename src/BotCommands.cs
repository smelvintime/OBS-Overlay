// Chat commands: what the bot knows how to answer.
//
// Two kinds. A handful are "live" - they have to go and find something out, so
// they are code: what is playing, how long the stream has been up, how long
// someone has followed. Everything else is just text the streamer wants said,
// which is data, not code.
//
// That split is the whole design. Guessing which ten commands a League streamer
// wants and hard-coding them would be wrong for the next streamer, so the text
// ones are editable, addable and removable from the dashboard without a rebuild.
// The defaults below are a starting point, not a fixed menu.
//
// Definitions live in %APPDATA%\NowPlayingOverlay\bot-commands.json rather than
// in twitch-config.json. The app rewrites this file every time a toggle moves,
// and twitch-config.json holds live OAuth tokens - a bad write there could cost
// the user their credentials, so the app never writes to it at all.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NowPlaying {

  static class BotCommands {

    internal class Cmd {
      public string Name = "";          // without the "!"
      public string Aliases = "";       // comma separated, without "!"
      public string Response = "";      // text commands only
      public string Builtin = "";       // song|uptime|followage|shoutout|commands, else text
      public bool Enabled = true;
      public int Cooldown = 10;         // seconds, per command
      public bool ModOnly;
      public DateTime LastUsed = DateTime.MinValue;
    }

    static readonly object _lock = new object();
    static readonly List<Cmd> _cmds = new List<Cmd>();

    static string Path_() {
      string dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return System.IO.Path.Combine(dir, "bot-commands.json");
    }

    // ------------------------------------------------------------- defaults
    // Seeded once on first run. Anything the streamer has to fill in ships
    // disabled, so the bot never answers "!discord" with a placeholder in front
    // of a live audience.
    static List<Cmd> Defaults() {
      return new List<Cmd> {
        new Cmd { Name = "song",      Builtin = "song",      Aliases = "nowplaying,music", Cooldown = 10 },
        new Cmd { Name = "uptime",    Builtin = "uptime",    Cooldown = 15 },
        new Cmd { Name = "followage", Builtin = "followage", Aliases = "followtime", Cooldown = 10 },
        new Cmd { Name = "commands",  Builtin = "commands",  Aliases = "help,cmds", Cooldown = 20 },
        new Cmd { Name = "so",        Builtin = "shoutout",  Aliases = "shoutout", Cooldown = 0, ModOnly = true },

        // League. Links and text beat a Riot API key that expires every 24 hours
        // and takes an approved application to make permanent.
        new Cmd { Name = "opgg",  Response = "https://op.gg/summoners/na/YOUR-NAME-HERE", Enabled = false },
        new Cmd { Name = "rank",  Aliases = "elo,lp", Response = "Currently: set this in the dashboard", Enabled = false },
        new Cmd { Name = "runes", Response = "Runes for this game: set this in the dashboard", Enabled = false },
        new Cmd { Name = "build", Aliases = "builds,items", Response = "Build: set this in the dashboard", Enabled = false },
        new Cmd { Name = "champ", Aliases = "champion", Response = "Playing: set this in the dashboard", Enabled = false },
        new Cmd { Name = "role",  Response = "Main role: set this in the dashboard", Enabled = false },

        // Community boilerplate every channel ends up wanting.
        new Cmd { Name = "discord",  Response = "Discord: set this in the dashboard", Enabled = false },
        new Cmd { Name = "socials",  Aliases = "social", Response = "Socials: set this in the dashboard", Enabled = false },
        new Cmd { Name = "lurk",     Response = "Thanks for the lurk, enjoy your stay.", Enabled = false },
        new Cmd { Name = "prime",    Response = "Free sub with Twitch Prime every month - it helps a lot.", Enabled = false },
        new Cmd { Name = "schedule", Response = "Stream schedule: set this in the dashboard", Enabled = false },
        new Cmd { Name = "specs",    Response = "Specs: set this in the dashboard", Enabled = false },
        new Cmd { Name = "sens",     Aliases = "sensitivity", Response = "Sens / settings: set this in the dashboard", Enabled = false }
      };
    }

    // ------------------------------------------------------------------- I/O
    public static void Load() {
      lock (_lock) {
        _cmds.Clear();
        try {
          string p = Path_();
          if (File.Exists(p)) {
            var raw = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(p)) as object[];
            if (raw != null) {
              foreach (var item in raw) {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                _cmds.Add(new Cmd {
                  Name = S(d, "name").Trim().TrimStart('!').ToLowerInvariant(),
                  Aliases = S(d, "aliases"),
                  Response = S(d, "response"),
                  Builtin = S(d, "builtin"),
                  Enabled = B(d, "enabled"),
                  Cooldown = N(d, "cooldown", 10),
                  ModOnly = B(d, "modOnly")
                });
              }
            }
          }
        } catch (Exception ex) {
          AppLog.Write("chat: could not read bot-commands.json: " + ex.Message);
        }
        if (_cmds.Count == 0) { _cmds.AddRange(Defaults()); SaveLocked(); }
      }
    }

    public static void Save() { lock (_lock) SaveLocked(); }

    static void SaveLocked() {
      try {
        var list = new List<object>();
        foreach (var c in _cmds) {
          list.Add(new Dictionary<string, object> {
            {"name", c.Name}, {"aliases", c.Aliases}, {"response", c.Response},
            {"builtin", c.Builtin}, {"enabled", c.Enabled},
            {"cooldown", c.Cooldown}, {"modOnly", c.ModOnly}
          });
        }
        File.WriteAllText(Path_(), new JavaScriptSerializer().Serialize(list));
      } catch (Exception ex) {
        AppLog.Write("chat: could not write bot-commands.json: " + ex.Message);
      }
    }

    static string S(Dictionary<string, object> d, string k) {
      object v; return (d.TryGetValue(k, out v) && v != null) ? Convert.ToString(v) : "";
    }
    static bool B(Dictionary<string, object> d, string k) {
      object v; if (!d.TryGetValue(k, out v) || v == null) return false;
      try { return Convert.ToBoolean(v); } catch { return false; }
    }
    static int N(Dictionary<string, object> d, string k, int dflt) {
      object v; if (!d.TryGetValue(k, out v) || v == null) return dflt;
      try { return Convert.ToInt32(v); } catch { return dflt; }
    }

    // ---------------------------------------------------------------- editing
    public static bool SetEnabled(string name, bool on) {
      lock (_lock) {
        var c = Find(name);
        if (c == null) return false;
        c.Enabled = on;
        SaveLocked();
        return true;
      }
    }

    public static bool Update(string name, string response, int cooldown, bool modOnly, string aliases) {
      lock (_lock) {
        var c = Find(name);
        if (c == null) return false;
        if (response != null) c.Response = response;
        if (cooldown >= 0) c.Cooldown = cooldown;
        if (aliases != null) c.Aliases = aliases;
        c.ModOnly = modOnly;
        SaveLocked();
        return true;
      }
    }

    public static bool Add(string name, string response) {
      name = (name ?? "").Trim().TrimStart('!').ToLowerInvariant();
      if (name.Length == 0) return false;
      lock (_lock) {
        if (Find(name) != null) return false;
        _cmds.Add(new Cmd { Name = name, Response = response ?? "", Enabled = true, Cooldown = 10 });
        SaveLocked();
        return true;
      }
    }

    public static bool Remove(string name) {
      lock (_lock) {
        var c = Find(name);
        if (c == null) return false;
        _cmds.Remove(c);
        SaveLocked();
        return true;
      }
    }

    // Deleting a built-in is allowed, but its behaviour can't be recreated by
    // typing the name back in - this is the way back. Re-adds any default
    // (built-in or starter text command) whose name is gone, leaving everything
    // the streamer kept or customised untouched.
    public static int RestoreMissingDefaults() {
      lock (_lock) {
        int added = 0;
        foreach (var d in Defaults()) {
          if (Find(d.Name) == null) { _cmds.Add(d); added++; }
        }
        if (added > 0) SaveLocked();
        return added;
      }
    }

    static Cmd Find(string name) {
      name = (name ?? "").Trim().TrimStart('!').ToLowerInvariant();
      foreach (var c in _cmds) if (c.Name == name) return c;
      return null;
    }

    // -------------------------------------------------------------- dispatch
    public static bool TryRespond(TwitchChat.IrcEvent ev, out string reply, out string matched) {
      reply = null; matched = null;

      string msg = (ev.Message ?? "").Trim();
      // Users paste zero-width and replacement characters constantly; a trailing
      // one should not stop "!song" being recognised as "!song".
      msg = msg.TrimEnd(' ', '\r', '\n', '�', '​', '᠎');
      if (msg.Length < 2 || msg[0] != '!') return false;

      int sp = msg.IndexOf(' ');
      string word = (sp > 0 ? msg.Substring(1, sp - 1) : msg.Substring(1)).ToLowerInvariant();
      string rest = sp > 0 ? msg.Substring(sp + 1).Trim() : "";

      Cmd cmd = null;
      lock (_lock) {
        foreach (var c in _cmds) {
          if (!c.Enabled) continue;
          if (c.Name == word) { cmd = c; break; }
          foreach (var a in c.Aliases.Split(',')) {
            if (a.Trim().TrimStart('!').ToLowerInvariant() == word && word.Length > 0) { cmd = c; break; }
          }
          if (cmd != null) break;
        }
        if (cmd == null) return false;
        if (cmd.ModOnly && !ev.IsMod) return false;

        double since = (DateTime.Now - cmd.LastUsed).TotalSeconds;
        if (since < cmd.Cooldown) return false;
        cmd.LastUsed = DateTime.Now;
        matched = cmd.Name;
      }

      reply = Render(cmd, ev, rest);
      return !string.IsNullOrEmpty(reply);
    }

    static string Render(Cmd cmd, TwitchChat.IrcEvent ev, string rest) {
      switch (cmd.Builtin) {
        case "song":      return SongLine();
        case "uptime":    return UptimeLine();
        case "followage": return FollowAgeLine(ev);
        case "shoutout":  return ShoutoutLine(rest);
        case "commands":  return CommandsLine();
        default:
          return cmd.Response
            .Replace("{user}", ev.Nick)
            .Replace("{channel}", "");
      }
    }

    // ------------------------------------------------------------- built-ins
    static string SongLine() {
      var np = Program.CurrentSnapshot();
      if (np == null || np.Title.Length == 0) return "Nothing playing right now.";
      string s = np.Title + (np.Artist.Length > 0 ? " - " + np.Artist : "");
      return (np.Playing ? "Now playing: " : "Paused: ") + s;
    }

    static string UptimeLine() {
      if (!TwitchEvents.ApiReady) return "Can't check that right now.";
      int http;
      string body = TwitchEvents.Helix(
        "https://api.twitch.tv/helix/streams?user_id=" + TwitchEvents.BroadcasterId, out http);
      if (body == null) return "Can't check that right now.";
      var data = TwitchEvents.NavPublic(body, "data") as object[];
      if (data == null || data.Length == 0) return "Stream is offline.";
      string started = TwitchEvents.SNavPublic(data[0], "started_at");
      DateTime t;
      if (!DateTime.TryParse(started, CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
        return "Stream is live.";
      return "Live for " + Span(DateTime.UtcNow - t) + ".";
    }

    static string FollowAgeLine(TwitchChat.IrcEvent ev) {
      if (!TwitchEvents.ApiReady || ev.UserId.Length == 0) return "Can't check that right now.";
      int http;
      string body = TwitchEvents.Helix(
        "https://api.twitch.tv/helix/channels/followers?broadcaster_id=" + TwitchEvents.BroadcasterId
        + "&user_id=" + ev.UserId, out http);
      if (body == null) return "Can't check that right now.";
      var data = TwitchEvents.NavPublic(body, "data") as object[];
      if (data == null || data.Length == 0) return ev.Nick + " isn't following yet.";
      DateTime t;
      if (!DateTime.TryParse(TwitchEvents.SNavPublic(data[0], "followed_at"), CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
        return ev.Nick + " is following.";
      return ev.Nick + " has been following for " + Span(DateTime.UtcNow - t) + ".";
    }

    static string ShoutoutLine(string rest) {
      string who = (rest ?? "").Trim().TrimStart('@').Split(' ')[0].ToLowerInvariant();
      if (who.Length == 0) return null;
      string url = "https://twitch.tv/" + who;
      if (!TwitchEvents.ApiReady) return "Go check out " + who + " over at " + url;

      int http;
      string users = TwitchEvents.Helix(
        "https://api.twitch.tv/helix/users?login=" + Uri.EscapeDataString(who), out http);
      var ud = users == null ? null : TwitchEvents.NavPublic(users, "data") as object[];
      if (ud == null || ud.Length == 0) return "Go check out " + who + " over at " + url;

      string id = TwitchEvents.SNavPublic(ud[0], "id");
      string name = TwitchEvents.SNavPublic(ud[0], "display_name");
      if (name.Length == 0) name = who;

      string ch = TwitchEvents.Helix(
        "https://api.twitch.tv/helix/channels?broadcaster_id=" + id, out http);
      var cd = ch == null ? null : TwitchEvents.NavPublic(ch, "data") as object[];
      string game = (cd != null && cd.Length > 0) ? TwitchEvents.SNavPublic(cd[0], "game_name") : "";

      return "Go check out " + name + (game.Length > 0 ? ", last seen playing " + game : "")
           + " - https://twitch.tv/" + who;
    }

    static string CommandsLine() {
      var names = new List<string>();
      lock (_lock) {
        foreach (var c in _cmds) if (c.Enabled && !c.ModOnly) names.Add("!" + c.Name);
      }
      if (names.Count == 0) return "No commands are switched on.";
      return "Commands: " + string.Join(" ", names.ToArray());
    }

    static string Span(TimeSpan t) {
      if (t.TotalSeconds < 60) return "less than a minute";
      var parts = new List<string>();
      int d = (int)t.TotalDays, h = t.Hours, m = t.Minutes;
      if (d > 0) parts.Add(d + (d == 1 ? " day" : " days"));
      if (h > 0) parts.Add(h + (h == 1 ? " hour" : " hours"));
      if (m > 0 && d == 0) parts.Add(m + (m == 1 ? " minute" : " minutes"));
      return string.Join(", ", parts.ToArray());
    }

    // ------------------------------------------------------------------ JSON
    public static string Json() {
      var sb = new StringBuilder("[");
      lock (_lock) {
        for (int i = 0; i < _cmds.Count; i++) {
          var c = _cmds[i];
          if (i > 0) sb.Append(',');
          sb.Append("{\"name\":").Append(TwitchChat.Qs(c.Name))
            .Append(",\"aliases\":").Append(TwitchChat.Qs(c.Aliases))
            .Append(",\"response\":").Append(TwitchChat.Qs(c.Response))
            .Append(",\"builtin\":").Append(TwitchChat.Qs(c.Builtin))
            .Append(",\"enabled\":").Append(c.Enabled ? "true" : "false")
            .Append(",\"cooldown\":").Append(c.Cooldown)
            .Append(",\"modOnly\":").Append(c.ModOnly ? "true" : "false")
            .Append('}');
        }
      }
      return sb.Append(']').ToString();
    }
  }
}
