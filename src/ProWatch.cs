// !pros: which pros and streamers are in the current game.
//
// Three ways of knowing, tried in this order for each of the nine other
// players, and every one of them an exact match:
//
//   1. The pro list - data/pros.json, built into the exe. Riot IDs with their
//      #tag, each one an account a pro is known to play on. Seeded from
//      Tracking The Pros (trackingthepros.com), edited like any other file in
//      the repo, and on every install within minutes of an edit because the
//      app updates itself from the source.
//   2. Players met before - %APPDATA%\NowPlayingOverlay\pros-seen.json, keyed
//      by puuid. Riot IDs change; puuids never do. So a pro found once by
//      their listed name is still found after a rename, and a streamer found
//      once on Twitch is still known on a day they are not live.
//   3. Twitch - the player's name turned into the Twitch logins it could be
//      ("TTV Some Streamer" -> somestreamer, some_streamer, ...) and Twitch
//      asked, once for the whole lobby, which of those channels is live in
//      League right now. A channel with that exact name, live in League at
//      the very minute this player is in a League game, is the player.
//
// The call-out names a real person in front of the whole chat, so the rules
// lean the way GhostWatch's do: a missed pro costs nothing, a wrong one is
// a stranger told they are someone. No fuzzy names, no partial matches, no
// list entry without its tag, no Twitch login shorter than four characters,
// and a live channel two players could both be is claimed by neither.
//
// STRICTLY READ-ONLY toward the League client, like everything that reads it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NowPlaying {

  static class ProWatch {

    internal class Pro {
      public string Name = "", Team = "", Role = "", Twitch = "";
      public string From = "list";     // list | twitch - how this person was first known
      public string RiotId = "";       // memory only: the name they were last seen under
    }

    internal class Find {
      public bool Ally, Live;
      public Pro Pro;
    }

    static readonly object _askLock = new object();

    // ------------------------------------------------------------------ list
    // Loaded once from the exe. A broken file is a log line and an empty list,
    // never a broken command: the other two ways of knowing still work.
    static Dictionary<string, Pro> _list;

    static Dictionary<string, Pro> List() {
      if (_list != null) return _list;
      var d = new Dictionary<string, Pro>();
      try {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pros.json")) {
          if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) d = ParseList(r.ReadToEnd());
        }
      } catch (Exception ex) {
        AppLog.Write("pros: could not read the pro list: " + ex.Message);
      }
      AppLog.Write("pros: " + d.Count + " known pro accounts");
      _list = d;
      return d;
    }

    // { "pros": [ { "name", "team", "role", "twitch", "accounts": ["Name#TAG", ...] } ] }
    // One row per person, so adding an account found mid-stream is adding a
    // string to their list. An account without a #tag is skipped: a bare name
    // is not an account.
    internal static Dictionary<string, Pro> ParseList(string json) {
      var d = new Dictionary<string, Pro>();
      var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json)
                 as Dictionary<string, object>;
      object rows;
      if (root == null || !root.TryGetValue("pros", out rows) || !(rows is object[])) return d;
      foreach (var o in (object[])rows) {
        var row = o as Dictionary<string, object>;
        if (row == null) continue;
        var pro = new Pro { Name = S(row, "name"), Team = S(row, "team"), Role = S(row, "role"),
                            Twitch = S(row, "twitch").ToLowerInvariant() };
        object accts;
        if (pro.Name.Length == 0 || !row.TryGetValue("accounts", out accts) || !(accts is object[])) continue;
        foreach (var a in (object[])accts) {
          string k = Key(Convert.ToString(a));
          if (k.IndexOf('#') > 0 && !d.ContainsKey(k)) d[k] = pro;
        }
      }
      return d;
    }

    // Riot IDs compare without case and without the spaces either side of
    // the #, and nothing else: "Concrete jungle#USA" is not "Concretejungle#USA".
    internal static string Key(string riotId) {
      if (riotId == null) return "";
      int h = riotId.IndexOf('#');
      if (h < 0) return riotId.Trim().ToLowerInvariant();
      return (riotId.Substring(0, h).Trim() + "#" + riotId.Substring(h + 1).Trim()).ToLowerInvariant();
    }

    static string S(Dictionary<string, object> d, string k) {
      object v; return (d.TryGetValue(k, out v) && v != null) ? Convert.ToString(v).Trim() : "";
    }

    // ------------------------------------------------------------ remembered
    static Dictionary<string, Pro> _seen;

    static string SeenPath() {
      string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NowPlayingOverlay");
      Directory.CreateDirectory(dir);
      return Path.Combine(dir, "pros-seen.json");
    }

    static Dictionary<string, Pro> Seen() {
      if (_seen != null) return _seen;
      var d = new Dictionary<string, Pro>();
      try {
        string p = SeenPath();
        if (File.Exists(p)) {
          var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(p))
                     as Dictionary<string, object>;
          if (root != null)
            foreach (var kv in root) {
              var row = kv.Value as Dictionary<string, object>;
              if (row == null || S(row, "name").Length == 0) continue;
              d[kv.Key] = new Pro { Name = S(row, "name"), Team = S(row, "team"), Role = S(row, "role"),
                                    Twitch = S(row, "twitch").ToLowerInvariant(), RiotId = S(row, "riotId"),
                                    From = S(row, "from") == "twitch" ? "twitch" : "list" };
            }
        }
      } catch (Exception ex) {
        AppLog.Write("pros: could not read pros-seen.json: " + ex.Message);
      }
      _seen = d;
      return d;
    }

    static void SaveSeen(Dictionary<string, Pro> seen) {
      try {
        var o = new Dictionary<string, object>();
        foreach (var kv in seen) {
          o[kv.Key] = new Dictionary<string, object> {
            {"name", kv.Value.Name}, {"team", kv.Value.Team}, {"role", kv.Value.Role},
            {"twitch", kv.Value.Twitch}, {"from", kv.Value.From}, {"riotId", kv.Value.RiotId}
          };
        }
        Files.WriteAtomic(SeenPath(), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o));
      } catch (Exception ex) {
        AppLog.Write("pros: could not write pros-seen.json: " + ex.Message);
      }
    }

    // ---------------------------------------------------------------- Twitch
    // The Twitch logins a Riot name could be. Twitch logins are 4-25 of
    // a-z, 0-9 and _, so a Riot name has two honest spellings there: run
    // together, or with underscores where its spaces were. Each of those
    // with and without a TTV/twitch tag - the tag's whole meaning is "the
    // rest of this is my Twitch name" - but only a tag that is its own word:
    // "TTV Name", "Name_ttv", "TTVName", "NameTTV". "ttvalentine" is not
    // a tag on "alentine". Nothing else: no guessing at what a decorated
    // name "really" is, and a name with letters Twitch cannot spell has no
    // login at all.
    static readonly Regex LoginShape = new Regex("^[a-z0-9][a-z0-9_]{3,24}$");
    static readonly string[] StreamTags = { "twitchtv", "twitch", "ttv" };

    internal static List<string> TwitchLogins(string riotId) {
      var list = new List<string>();
      if (string.IsNullOrEmpty(riotId)) return list;
      int h = riotId.IndexOf('#');
      string name = (h >= 0 ? riotId.Substring(0, h) : riotId).Trim();
      if (!Spell(name, list)) return list;
      foreach (var tag in StreamTags) {
        int n = name.Length, t = tag.Length;
        if (n <= t) continue;
        if (name.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) {
          char next = name[t];
          if (next == ' ' || next == '_' || char.IsUpper(next)) Spell(name.Substring(t), list);
        }
        if (name.EndsWith(tag, StringComparison.OrdinalIgnoreCase)) {
          char before = name[n - t - 1], first = name[n - t];
          if (before == ' ' || before == '_' || (char.IsUpper(first) && !char.IsUpper(before)))
            Spell(name.Substring(0, n - t), list);
        }
      }
      return list;
    }

    // Both spellings of one name into the list; false when the name holds a
    // character Twitch logins cannot carry.
    static bool Spell(string name, List<string> list) {
      var joined = new StringBuilder();
      var under = new StringBuilder();
      foreach (char raw in name) {
        char c = char.ToLowerInvariant(raw);
        if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) { joined.Append(c); under.Append(c); }
        else if (c == ' ' || c == '_') { if (under.Length > 0 && under[under.Length - 1] != '_') under.Append('_'); }
        else return false;
      }
      Add(list, joined.ToString());
      Add(list, under.ToString().Trim('_'));
      return true;
    }

    static void Add(List<string> list, string login) {
      if (LoginShape.IsMatch(login) && !list.Contains(login)) list.Add(login);
    }

    // League of Legends on Twitch.
    const string LeagueGameId = "21779";

    // Helix's stream list down to the channels live in League, login -> the
    // display name they go by.
    internal static Dictionary<string, string> LiveInLeague(string helixBody) {
      var live = new Dictionary<string, string>();
      var data = TwitchEvents.NavPublic(helixBody, "data") as object[];
      if (data == null) return live;
      foreach (var s in data) {
        if (TwitchEvents.SNavPublic(s, "game_id") != LeagueGameId) continue;
        if (TwitchEvents.SNavPublic(s, "type") != "live") continue;
        string login = TwitchEvents.SNavPublic(s, "user_login").ToLowerInvariant();
        if (login.Length == 0) continue;
        string shown = TwitchEvents.SNavPublic(s, "user_name");
        live[login] = shown.Length > 0 ? shown : login;
      }
      return live;
    }

    // One ask per game, refreshed at most every two minutes so a streamer who
    // goes live mid-game is picked up, and again at once if the lobby itself
    // changed (a roster that was still filling in on the first ask).
    static long _liveGame;
    static string _liveKey = "";
    static DateTime _liveAt = DateTime.MinValue;
    static Dictionary<string, string> _live = new Dictionary<string, string>();

    static Dictionary<string, string> LiveNow(long gameId, List<string> logins) {
      if (!TwitchEvents.ApiReady || logins.Count == 0) return new Dictionary<string, string>();
      var sorted = new List<string>(logins); sorted.Sort(StringComparer.Ordinal);
      string key = string.Join(",", sorted.ToArray());
      if (gameId == _liveGame && key == _liveKey && (DateTime.UtcNow - _liveAt).TotalSeconds < 120)
        return _live;
      var live = new Dictionary<string, string>();
      for (int i = 0; i < sorted.Count; i += 100) {
        var url = new StringBuilder("https://api.twitch.tv/helix/streams?type=live&first=100");
        for (int j = i; j < sorted.Count && j < i + 100; j++)
          url.Append("&user_login=").Append(Uri.EscapeDataString(sorted[j]));
        int http;
        string body = TwitchEvents.Helix(url.ToString(), out http);
        // A failed ask is not "nobody is live" - keep the last answer for this
        // game and try again on the next one.
        if (body == null) return gameId == _liveGame ? _live : live;
        foreach (var kv in LiveInLeague(body)) live[kv.Key] = kv.Value;
      }
      _liveGame = gameId; _liveKey = key; _liveAt = DateTime.UtcNow; _live = live;
      return live;
    }

    // ------------------------------------------------------------- deciding
    // The Twitch logins worth asking about: a known person's own channel, and
    // for everyone else the spellings their name allows.
    internal static List<string> Candidates(List<LobbyRanks.Player> players, Dictionary<string, Pro> list,
                                            Dictionary<string, Pro> seen) {
      var all = new List<string>();
      foreach (var p in players) {
        if (p.Hidden) continue;
        Pro known = Known(p, list, seen);
        if (known != null) {
          if (known.Twitch.Length > 0 && !all.Contains(known.Twitch)) all.Add(known.Twitch);
          continue;
        }
        foreach (var l in TwitchLogins(p.RiotId)) if (!all.Contains(l)) all.Add(l);
      }
      return all;
    }

    // The list first - it carries today's team - then the puuid memory.
    static Pro Known(LobbyRanks.Player p, Dictionary<string, Pro> list, Dictionary<string, Pro> seen) {
      Pro pro;
      if (p.RiotId.IndexOf('#') > 0 && list.TryGetValue(Key(p.RiotId), out pro)) return pro;
      if (p.Puuid.Length > 0 && seen.TryGetValue(p.Puuid, out pro)) return pro;
      return null;
    }

    // Who is who, from the three sources, plus what memory should learn.
    // Pure: everything it needs is passed in, so the checks can run it.
    internal static List<Find> Resolve(List<LobbyRanks.Player> players, Dictionary<string, Pro> list,
                                       Dictionary<string, Pro> seen, Dictionary<string, string> live,
                                       Dictionary<string, Pro> learned) {
      // A live login more than one player's name could be is nobody's, and
      // neither is one that already belongs to a known person in this lobby.
      var claims = new Dictionary<string, int>();
      foreach (var p in players) {
        if (p.Hidden) continue;
        Pro known = Known(p, list, seen);
        if (known != null) {
          if (known.Twitch.Length > 0) claims[known.Twitch] = 99;
          continue;
        }
        foreach (var l in TwitchLogins(p.RiotId))
          if (live.ContainsKey(l)) { int n; claims.TryGetValue(l, out n); claims[l] = n + 1; }
      }

      var finds = new List<Find>();
      foreach (var p in players) {
        if (p.Hidden) continue;
        Pro pro = Known(p, list, seen);
        bool isLive = false;
        if (pro != null) {
          isLive = pro.Twitch.Length > 0 && live.ContainsKey(pro.Twitch);
          // A list match teaches memory this puuid, so a rename keeps them,
          // and a known puuid under a new name refreshes the name on file.
          Pro had;
          if (p.Puuid.Length > 0 && (!seen.TryGetValue(p.Puuid, out had) || !Same(had, pro) || had.RiotId != p.RiotId))
            learned[p.Puuid] = Copy(pro, p.RiotId);
        } else {
          foreach (var l in TwitchLogins(p.RiotId)) {
            int n;
            if (!claims.TryGetValue(l, out n) || n != 1) continue;
            pro = new Pro { Name = live[l], Twitch = l, From = "twitch", RiotId = p.RiotId };
            isLive = true;
            if (p.Puuid.Length > 0) learned[p.Puuid] = pro;
            break;
          }
        }
        if (pro != null) finds.Add(new Find { Ally = p.Ally, Pro = pro, Live = isLive });
      }
      return finds;
    }

    static bool Same(Pro a, Pro b) {
      return a.Name == b.Name && a.Team == b.Team && a.Role == b.Role && a.Twitch == b.Twitch && a.From == b.From;
    }

    // Memory gets its own copy: list entries are shared by every account a
    // pro has, and the Riot ID on file is this one account's.
    static Pro Copy(Pro p, string riotId) {
      return new Pro { Name = p.Name, Team = p.Team, Role = p.Role, Twitch = p.Twitch, From = p.From, RiotId = riotId ?? "" };
    }

    // ------------------------------------------------------------------ chat
    // "Canyon (Gen.G Jungle)", "SomeStreamer (live on Twitch)",
    // "SomeStreamer (streamer)" for one met on Twitch before and not live now.
    internal static string Label(Pro p, bool live, bool link) {
      var bits = new List<string>();
      string role = p.Role.Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : p.Role;
      string tr = (p.Team + " " + role).Trim();
      if (tr.Length > 0) bits.Add(tr);
      if (live) bits.Add("live on Twitch");
      else if (p.From == "twitch") bits.Add("streamer");
      string s = p.Name + (bits.Count > 0 ? " (" + string.Join(", ", bits.ToArray()) + ")" : "");
      if (live && link && p.Twitch.Length > 0) s += " twitch.tv/" + p.Twitch;
      return s;
    }

    // Us first, the way !ranks reads. An enemy's stream is named but not
    // linked until the game is over: a link mid-game is an invitation to
    // watch the other side's screen.
    internal static string Line(List<Find> finds, int checkedCount, int hidden, bool over, bool past) {
      string hid = hidden == 1 ? "1 player hidden" : hidden + " players hidden";
      if (finds.Count == 0) {
        string none = "no pros or streamers spotted - " + checkedCount + " player" + (checkedCount == 1 ? "" : "s")
                    + " checked" + (hidden > 0 ? ", " + hid : "") + ".";
        return past ? "Last game - " + none : char.ToUpperInvariant(none[0]) + none.Substring(1);
      }
      var us = new List<string>(); var them = new List<string>();
      foreach (var f in finds)
        (f.Ally ? us : them).Add(Label(f.Pro, f.Live, f.Ally || over || past));
      var parts = new List<string>();
      if (us.Count > 0) parts.Add("Us - " + string.Join(", ", us.ToArray()));
      if (them.Count > 0) parts.Add("Them - " + string.Join(", ", them.ToArray()));
      string line = (past ? "Last game - " : "Pros/streamers in this game: ")
                  + string.Join(" | ", parts.ToArray()) + (hidden > 0 ? " (" + hid + ")" : "");
      // One chat message. Ten long labels cannot all fit; the cut lands
      // between two names rather than through one.
      if (line.Length > 490) {
        int cut = line.LastIndexOf(", ", 486, StringComparison.Ordinal);
        line = (cut > 0 ? line.Substring(0, cut) : line.Substring(0, 486)) + " ...";
      }
      return line;
    }

    // ---------------------------------------------------------------- !pros
    static List<Find> _lastFinds;
    static int _lastChecked, _lastHidden;

    public static string CommandLine() {
      lock (_askLock) {
        try {
          long gid; List<LobbyRanks.Player> players; bool over; string why;
          if (!LobbyRanks.PlayersNow(out gid, out players, out over, out why)) {
            if (why == "no-client") return "The League client isn't running on the stream PC.";
            if (why == "no-phase") return "Can't reach the League client right now.";
            if (why == "not-ready") return "Reading the lobby - ask again in a moment.";
            if (why == "champ-select") return "Pros show up once the game loads.";
            // Between games the last match still answers, honestly labelled
            // as past - the same rule !ranks and !ghosts follow.
            if (_lastFinds != null) return Line(_lastFinds, _lastChecked, _lastHidden, true, true);
            return "Not in a game right now - pros show up once a game loads.";
          }

          var list = List();
          var seen = Seen();
          var live = LiveNow(gid, Candidates(players, list, seen));
          var learned = new Dictionary<string, Pro>();
          var finds = Resolve(players, list, seen, live, learned);

          if (learned.Count > 0) {
            foreach (var kv in learned) {
              seen[kv.Key] = kv.Value;
              AppLog.Write("pros: remembered " + kv.Value.Name + " as " + kv.Value.RiotId + " (" + kv.Value.From + ")");
            }
            SaveSeen(seen);
          }

          int hidden = 0;
          foreach (var p in players) if (p.Hidden) hidden++;
          _lastFinds = finds; _lastChecked = players.Count - hidden; _lastHidden = hidden;
          return Line(finds, _lastChecked, hidden, over, false);
        } catch (Exception ex) {
          AppLog.Write("pros: " + ex.Message);
          return "Can't read the lobby right now.";
        }
      }
    }
  }
}
